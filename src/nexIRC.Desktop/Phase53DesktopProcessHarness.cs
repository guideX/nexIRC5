using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking;

namespace nexIRC.Desktop;

/// <summary>
/// Bounded stdin/stdout control for the Phase 53 process acceptance fixture.
/// It is entered only with the explicit --phase53-process flag and uses the
/// real WPF composer, NetworkSessionManager, TLS transport, and protected store.
/// </summary>
internal static class Phase53DesktopProcessHarness
{
    private const string Flag = "--phase53-process";

    public static bool IsRequested(string[] args) => args.Any(argument =>
        string.Equals(argument, Flag, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The protected desktop process fixture requires Windows.");

        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024, leaveOpen: true));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true
        });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true
        });
        Emit("DESKTOP_PROCESS_HARNESS_STARTED");

        var options = Options.Parse(args);
        Emit("DESKTOP_OPTIONS_PARSED");
        var password = await Console.In.ReadLineAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Missing SASL fixture credential input.");
        Emit("DESKTOP_CREDENTIAL_INPUT_RECEIVED");

        var protector = new WindowsDpapiResumeSecretProtector();
        using var pinBeforeSendBarrier = OpenBarrier(options.PinBeforeSendBarrierName);
        using var resumeStore = new JsonResumeStateStore(options.ResumeStorePath);
        var draftStore = new ProtectedJsonConversationDraftStore(options.DraftStorePath, protector);
        Emit("DESKTOP_STORES_READY");
        var configuration = new ConfigurationService(new InMemoryConfigurationStore());
        configuration.SetPreferences(new ApplicationPreferences
        {
            ConversationLoggingEnabled = false,
            PrivateMessageLoggingEnabled = false,
            StatusLoggingEnabled = false
        });
        var credentials = new ProfileCredentialService(new InMemoryProfileCredentialStore());
        var transport = new TcpTlsIrcTransportFactory(new TcpTlsIrcTransportOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ServerCertificateValidationCallback = (_, certificate, _, _) => certificate is not null
                && string.Equals(certificate.GetCertHashString(), options.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
        });
        var window = new MainWindow(transport, configuration, credentials, conversationDraftStore: draftStore,
            resumeStateStore: resumeStore, resumeSecretProtector: protector);
        Emit("DESKTOP_WINDOW_CREATED");
        window.Show();
        Emit("DESKTOP_WINDOW_SHOWN");

        var requestedCapabilities = IrcCapabilityCatalog.PreferredPhase1Y;
        var workspace = window.ViewModel.Sessions.Add(new NetworkConnectionOptions
        {
            ProfileId = options.ProfileId,
            DisplayName = "Phase 53 desktop process fixture",
            Endpoint = new IrcEndpoint(options.Host, options.Port, useTls: true),
            Nickname = options.Nickname,
            Username = options.Username,
            RealName = "nexIRC Phase 53 desktop process fixture",
            DesiredChannels = new HashSet<string>(options.Channels, StringComparer.OrdinalIgnoreCase),
            RequestedCapabilities = requestedCapabilities,
            SaslPolicy = SaslAuthenticationPolicy.Required,
            SaslCredentialProvider = new FixtureCredentialProvider(options.Username, password),
            ResumeNetworkIdentity = $"phase53-process-profile:{options.ProfileId:N}",
            CreateNewAttachmentOnConnect = options.CreateAttachment,
            ResumeExistingAttachmentOnConnect = options.ResumeExistingAttachment,
            Reconnect = new ReconnectPolicy(Enabled: false)
        });
        var managerSnapshotApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var managerPinSnapshotApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        window.ViewModel.Sessions.SynchronizedDraftStateReceived += (_, draftEvent) =>
        {
            if (draftEvent.Snapshot is { } snapshot)
            {
                Emit($"DESKTOP_MANAGER_DRAFT_SNAPSHOT count={snapshot.Drafts.Count.ToString(CultureInfo.InvariantCulture)}");
                managerSnapshotApplied.TrySetResult();
            }
            else if (draftEvent.Draft is { } draft)
            {
                Emit($"DESKTOP_MANAGER_DRAFT_UPDATE revision={draft.Revision.ToString(CultureInfo.InvariantCulture)} bytes={Encoding.UTF8.GetByteCount(draft.Text).ToString(CultureInfo.InvariantCulture)}");
            }
        };

        window.ViewModel.Sessions.SynchronizedPinStateReceived += (_, pinEvent) =>
        {
            if (pinEvent.Status is { } status)
                Emit($"DESKTOP_PIN_ACK status={status} mutation={(pinEvent.MutationId is null ? "none" : "present")}");
            if (pinEvent.Snapshot is { } snapshot)
            {
                EmitPinSnapshot(snapshot);
                managerPinSnapshotApplied.TrySetResult();
            }
            else if (pinEvent.Pin is { } pin)
            {
                Emit($"DESKTOP_PIN_UPDATE revision={pinEvent.CatalogRevision?.ToString(CultureInfo.InvariantCulture) ?? "none"} entryRevision={pin.Revision.ToString(CultureInfo.InvariantCulture)} pinned={pin.IsPinned.ToString().ToLowerInvariant()} key={Fingerprint(pin.ConversationKey)[..16]}");
            }
        };

        workspace.Session.OutboundCommandSent += (_, outbound) =>
        {
            if (options.Channels.Any(channel => outbound.RawLine.StartsWith($"PRIVMSG {channel} :", StringComparison.OrdinalIgnoreCase)))
                Emit($"DESKTOP_IRC_PRIVMSG_WRITTEN bytes={outbound.RawBytes.Length.ToString(CultureInfo.InvariantCulture)}");
        };

        workspace.Session.SemanticEventReceived += (_, item) =>
        {
            if (item.Event is IrcNexIrcDraftStateEvent draftEvent)
            {
                if (draftEvent.Snapshot is { } snapshot)
                {
                    Emit($"DESKTOP_DRAFT_SNAPSHOT count={snapshot.Drafts.Count.ToString(CultureInfo.InvariantCulture)}");
                    foreach (var draft in snapshot.Drafts)
                        Emit($"DESKTOP_DRAFT_AUTH revision={draft.Revision.ToString(CultureInfo.InvariantCulture)} bytes={Encoding.UTF8.GetByteCount(draft.Text).ToString(CultureInfo.InvariantCulture)}");
                }
                else if (draftEvent.Draft is { } update)
                {
                    var sessionDiagnostics = window.ViewModel.Sessions.Diagnostics;
                    Emit($"DESKTOP_DRAFT_AUTH revision={update.Revision.ToString(CultureInfo.InvariantCulture)} bytes={Encoding.UTF8.GetByteCount(update.Text).ToString(CultureInfo.InvariantCulture)} generation={item.ConnectionGeneration} currentGeneration={workspace.Session.Snapshot.ConnectionGeneration} staleEvents={sessionDiagnostics.StaleGenerationEventsDiscarded.ToString(CultureInfo.InvariantCulture)} duplicateEvents={sessionDiagnostics.DuplicateSemanticEventsDiscarded.ToString(CultureInfo.InvariantCulture)}");
                }
                else if (draftEvent.MutationId is { } mutationId)
                {
                    Emit($"DESKTOP_DRAFT_ACK mutation={mutationId} status={draftEvent.Status ?? "unknown"} revision={draftEvent.AuthoritativeRevision?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
                }
            }
        };

        Emit($"DESKTOP_STARTING profile={options.ProfileId:N}");
        Emit("DESKTOP_CONNECTING");
        var connectTask = window.ViewModel.Sessions.ConnectAsync(workspace.Id).AsTask();
        _ = ObserveConnectTaskAsync(connectTask);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (workspace.Session.Snapshot.Registration == RegistrationState.Registered
                && workspace.Session.NativeResumeSession is not null
                && (!options.CreateAttachment || workspace.Session.NativeResumeSession.AttachmentId is not null)
                && (!options.ResumeExistingAttachment || workspace.Session.Continuity.State == ConnectionContinuityState.Synchronized)
                && options.Channels.All(requested => workspace.Channels.Any(channel => channel.Channel.Equals(requested, StringComparison.OrdinalIgnoreCase)))
                && workspace.Session.Snapshot.State is ServerSessionState.Connected or ServerSessionState.Registered)
                break;
            if (workspace.Session.Snapshot.State == ServerSessionState.Failed)
            {
                var failure = workspace.Session.Snapshot.LastFailure;
                var safeMessage = (failure?.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
                throw new InvalidOperationException($"The desktop process fixture session failed ({failure?.Kind.ToString() ?? "unknown"}: {safeMessage}).");
            }
            await Task.Delay(25).ConfigureAwait(true);
        }

        var activeChannel = workspace.Channels.FirstOrDefault(channel => channel.Channel.Equals(options.Channels[0], StringComparison.OrdinalIgnoreCase));
        if (activeChannel is null || workspace.Session.Snapshot.State is not (ServerSessionState.Connected or ServerSessionState.Registered))
            throw new TimeoutException($"The desktop process fixture did not reach readiness (state={workspace.Session.Snapshot.State}, registration={workspace.Session.Snapshot.Registration}, attachment={workspace.Session.NativeResumeSession?.AttachmentId is not null}, continuity={workspace.Session.Continuity.State}, channels={workspace.Channels.Count}).");

        await managerSnapshotApplied.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await managerPinSnapshotApplied.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        window.ViewModel.SelectView(activeChannel);
        await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        var managerHasDraftSnapshot = window.ViewModel.Sessions.TryGetSynchronizedDraftState(activeChannel, out var managerDraft);
        var diagnostics = window.ViewModel.Sessions.Diagnostics;
        if (!managerHasDraftSnapshot)
            throw new InvalidOperationException("The desktop Application did not apply its initial synchronized draft snapshot.");
        var caps = workspace.Session.Snapshot.Capabilities;
        Emit($"DESKTOP_CAPS resume={IsVersionEnabled(caps, "nexirc/resume", "1").ToString().ToLowerInvariant()} state={IsVersionEnabled(caps, "nexirc/state", "1").ToString().ToLowerInvariant()} pins={IsVersionEnabled(caps, "nexirc/pins", "1").ToString().ToLowerInvariant()}");
        Emit($"DESKTOP_READY view={activeChannel.Id:N} profile={options.ProfileId:N} attachment={workspace.Session.NativeResumeSession?.AttachmentId?.ToString("N") ?? "none"} managerSnapshot={managerHasDraftSnapshot.ToString().ToLowerInvariant()} managerRevision={managerDraft?.Revision.ToString(CultureInfo.InvariantCulture) ?? "none"} staleEvents={diagnostics.StaleGenerationEventsDiscarded.ToString(CultureInfo.InvariantCulture)} duplicateEvents={diagnostics.DuplicateSemanticEventsDiscarded.ToString(CultureInfo.InvariantCulture)}");

        // Console.In is a synchronous pipe for the external process fixture.
        // Read it off the UI thread so WPF continues applying real network
        // updates while the harness waits for its next bounded command.
        while (await Task.Run(() => Console.In.ReadLine()).ConfigureAwait(true) is { } command)
        {
            if (command.Length > 96 * 1024)
            {
                Emit("DESKTOP_COMMAND_RESULT outcome=TooLarge");
                continue;
            }

            if (command.StartsWith("GET_COMPOSER", StringComparison.OrdinalIgnoreCase))
            {
                var requestId = command.Length > "GET_COMPOSER".Length
                    ? command["GET_COMPOSER".Length..].Trim()
                    : Guid.NewGuid().ToString("N");
                await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                EmitComposerState(window, workspace, activeChannel, requestId);
            }
            else if (command.StartsWith("SET_COMPOSER ", StringComparison.OrdinalIgnoreCase))
            {
                var fields = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3)
                {
                    Emit("DESKTOP_COMPOSER_SET outcome=Malformed");
                    continue;
                }
                var requestId = fields[1];
                var text = Decode(fields[2]);
                if (Encoding.UTF8.GetByteCount(text) > 64 * 1024)
                {
                    Emit("DESKTOP_COMMAND_RESULT outcome=TextTooLarge");
                    continue;
                }

                window.ViewModel.InputText = text;
                Emit($"DESKTOP_COMPOSER_SET request={requestId} bytes={Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture)}");
            }
            else if (command.StartsWith("KEEP_SERVER", StringComparison.OrdinalIgnoreCase))
            {
                window.ViewModel.KeepServerDraftCommand.Execute(null);
                EmitComposerState(window, workspace, activeChannel, CommandRequestId(command));
            }
            else if (command.StartsWith("REPLACE_SERVER", StringComparison.OrdinalIgnoreCase))
            {
                window.ViewModel.ReplaceServerDraftCommand.Execute(null);
                EmitComposerState(window, workspace, activeChannel, CommandRequestId(command));
            }
            else if (command.Equals("SUBMIT", StringComparison.OrdinalIgnoreCase))
            {
                await window.ViewModel.SubmitInputAsync().ConfigureAwait(true);
                Emit($"DESKTOP_SUBMIT bytes={Encoding.UTF8.GetByteCount(window.ViewModel.InputText).ToString(CultureInfo.InvariantCulture)}");
            }
            else if (command.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase))
            {
                var channel = FindChannel(workspace, command["SELECT ".Length..]);
                window.ViewModel.SelectView(channel);
                await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                Emit($"DESKTOP_SELECTED channel={Fingerprint(channel.Channel)[..16]}");
            }
            else if (command.StartsWith("JOIN ", StringComparison.OrdinalIgnoreCase))
            {
                var fields = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var requestId = fields.Length == 3 ? fields[1] : Guid.NewGuid().ToString("N");
                var channelName = fields.Length == 3 ? fields[2].Trim() : fields[^1].Trim();
                if (string.IsNullOrWhiteSpace(channelName) || channelName.Length > 128)
                {
                    Emit($"DESKTOP_JOIN request={requestId} outcome=Invalid");
                    continue;
                }
                await workspace.Session.JoinChannelAsync(channelName).ConfigureAwait(true);
                var joinDeadline = DateTime.UtcNow.AddSeconds(10);
                ChannelView? joined = null;
                while (DateTime.UtcNow < joinDeadline)
                {
                    joined = workspace.Channels.FirstOrDefault(channel => channel.Channel.Equals(channelName, StringComparison.OrdinalIgnoreCase) && channel.IsJoined);
                    if (joined is not null) break;
                    await Task.Delay(25).ConfigureAwait(true);
                }
                Emit($"DESKTOP_JOIN request={requestId} outcome={(joined is null ? "Timeout" : "Ready")} channel={Fingerprint(channelName)[..16]}");
            }
            else if (command.StartsWith("PIN ", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("UNPIN ", StringComparison.OrdinalIgnoreCase))
            {
                var isPinned = command.StartsWith("PIN ", StringComparison.OrdinalIgnoreCase);
                var channel = FindChannel(workspace, command[(isPinned ? 4 : 6)..]);
                window.ViewModel.SelectView(channel);
                var changed = await window.ViewModel.SetConversationPinnedAsync(channel, isPinned).ConfigureAwait(true);
                Emit($"DESKTOP_PIN_REQUEST operation={(isPinned ? "PIN" : "UNPIN")} accepted={changed.ToString().ToLowerInvariant()}");
            }
            else if (command.StartsWith("PIN_VIEW ", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("UNPIN_VIEW ", StringComparison.OrdinalIgnoreCase))
            {
                var isPinned = command.StartsWith("PIN_VIEW ", StringComparison.OrdinalIgnoreCase);
                var channel = FindChannel(workspace, command[(isPinned ? 9 : 11)..]);
                var changed = await window.ViewModel.SetConversationPinnedAsync(channel, isPinned).ConfigureAwait(true);
                Emit($"DESKTOP_PIN_REQUEST operation={(isPinned ? "PIN" : "UNPIN")} accepted={changed.ToString().ToLowerInvariant()} selected={ReferenceEquals(window.ViewModel.ActiveView, channel).ToString().ToLowerInvariant()}");
            }
            else if (command.StartsWith("MOVE_TOP ", StringComparison.OrdinalIgnoreCase))
            {
                var channel = FindChannel(workspace, command["MOVE_TOP ".Length..]);
                var item = window.ViewModel.PinnedConversations.FirstOrDefault(candidate => candidate.View.Id == channel.Id);
                var changed = item is not null && await window.ViewModel.MovePinnedConversationToTopAsync(item).ConfigureAwait(true);
                Emit($"DESKTOP_PIN_REQUEST operation=MOVE_TOP accepted={changed.ToString().ToLowerInvariant()}");
            }
            else if (command.StartsWith("GET_PINS", StringComparison.OrdinalIgnoreCase))
            {
                await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                EmitPinCatalog(window, workspace, CommandRequestId(command));
            }
            else if (command.StartsWith("GET_PIN_PROJECTION", StringComparison.OrdinalIgnoreCase))
            {
                await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                EmitPinProjection(window, CommandRequestId(command));
            }
            else if (command.StartsWith("GET_PIN_ITEM ", StringComparison.OrdinalIgnoreCase))
            {
                var fields = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var requestId = fields.Length == 3 ? fields[1] : Guid.NewGuid().ToString("N");
                var channel = FindChannel(workspace, fields.Length == 3 ? fields[2] : fields[1]);
                if (window.ViewModel.Sessions.TryGetSynchronizedPin(workspace, channel, out var pin) && pin is not null)
                    Emit($"DESKTOP_PIN_ITEM request={requestId} key={Fingerprint(pin.ConversationKey)[..16]} pinned={pin.IsPinned.ToString().ToLowerInvariant()} revision={pin.Revision.ToString(CultureInfo.InvariantCulture)} order={pin.OrderSequence.ToString(CultureInfo.InvariantCulture)}");
                else
                    Emit($"DESKTOP_PIN_ITEM request={requestId} outcome=Unavailable");
            }
            else if (command.StartsWith("MUTATE_PIN ", StringComparison.OrdinalIgnoreCase))
            {
                var fields = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 5 || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expectedRevision)
                    || !NexIrcPinStateProtocol.IsValidMutationId(fields[4]))
                {
                    Emit("DESKTOP_PIN_REQUEST outcome=Malformed");
                    continue;
                }
                var channel = FindChannel(workspace, fields[2]);
                if (!window.ViewModel.Sessions.TryGetSynchronizedPinCatalog(workspace, out var catalog) || catalog is null
                    || NetworkSessionManager.SynchronizedPinConversationKey(channel, workspace.Snapshot) is not { } key)
                {
                    Emit("DESKTOP_PIN_REQUEST outcome=Unavailable");
                    continue;
                }
                var sent = await workspace.Session.TryMutateSynchronizedPinAsync(fields[1], catalog.Generation, key,
                    expectedRevision, fields[4], channel.Channel).ConfigureAwait(true);
                Emit($"DESKTOP_PIN_REQUEST operation={fields[1]} accepted={sent.ToString().ToLowerInvariant()} mutation=present");
            }
            else if (command.StartsWith("HOLD_PIN_BEFORE_SEND ", StringComparison.OrdinalIgnoreCase))
            {
                if (pinBeforeSendBarrier is null)
                {
                    Emit("DESKTOP_PIN_PREPARED outcome=BarrierUnavailable");
                    continue;
                }
                var channel = FindChannel(workspace, command["HOLD_PIN_BEFORE_SEND ".Length..]);
                Emit("DESKTOP_PIN_PREPARED outcome=Ready");
                var released = await Task.Run(() => pinBeforeSendBarrier.WaitOne(TimeSpan.FromSeconds(60))).ConfigureAwait(true);
                if (!released)
                {
                    Emit("DESKTOP_PIN_PREPARED outcome=Timeout");
                    continue;
                }
                Emit("DESKTOP_PIN_PREPARED outcome=Released");
                var changed = await window.ViewModel.SetConversationPinnedAsync(channel, true).ConfigureAwait(true);
                Emit($"DESKTOP_PIN_REQUEST operation=PIN accepted={changed.ToString().ToLowerInvariant()}");
            }
            else if (command.StartsWith("GET_PIN_WORKSPACE", StringComparison.OrdinalIgnoreCase))
            {
                await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                EmitPinWorkspace(window, workspace, CommandRequestId(command));
            }
            else if (command.StartsWith("WAIT_PINS ", StringComparison.OrdinalIgnoreCase))
            {
                var fields = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 4 || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                    || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var activeCount))
                {
                    Emit("DESKTOP_WAIT_PINS outcome=Malformed");
                    continue;
                }
                var until = DateTime.UtcNow.AddSeconds(15);
                NexIrcPinStateSnapshot? pinSnapshot = null;
                while (DateTime.UtcNow < until)
                {
                    await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                    if (window.ViewModel.Sessions.TryGetSynchronizedPinCatalog(workspace, out pinSnapshot)
                        && pinSnapshot is not null && pinSnapshot.CatalogRevision >= revision
                        && pinSnapshot.Entries.Count(static item => item.IsPinned) == activeCount)
                        break;
                    await Task.Delay(25).ConfigureAwait(true);
                }
                var ready = pinSnapshot is not null && pinSnapshot.CatalogRevision >= revision
                    && pinSnapshot.Entries.Count(static item => item.IsPinned) == activeCount;
                Emit($"DESKTOP_WAIT_PINS request={fields[1]} outcome={(ready ? "Ready" : "Timeout")} revision={pinSnapshot?.CatalogRevision.ToString(CultureInfo.InvariantCulture) ?? "none"} active={pinSnapshot?.Entries.Count(static item => item.IsPinned).ToString(CultureInfo.InvariantCulture) ?? "none"}");
            }
            else if (command.Equals("PAIR_CREATE", StringComparison.OrdinalIgnoreCase))
            {
                var authorization = await workspace.Session.RequestNativePairingAuthorizationAsync(
                    workspace.Session.Snapshot.ConnectionGeneration).ConfigureAwait(true);
                await WritePairingMaterialAsync(options.SecretPipeName, authorization.Material).ConfigureAwait(true);
                Emit("DESKTOP_PAIRING_READY");
            }
            else if (command.StartsWith("PAIR_USE ", StringComparison.OrdinalIgnoreCase))
            {
                var result = await workspace.Session.RequestNativeAttachmentWithPairingAsync(
                    workspace.Session.Snapshot.ConnectionGeneration,
                    command["PAIR_USE ".Length..]).ConfigureAwait(true);
                Emit($"DESKTOP_PAIR_RESULT outcome={result.Outcome} reason={result.RejectionReason?.ToString() ?? "none"}");
            }
            else if (command.StartsWith("WAIT_REVISION ", StringComparison.OrdinalIgnoreCase))
            {
                var fields = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
                {
                    Emit("DESKTOP_WAIT_REVISION outcome=Malformed");
                    continue;
                }
                var requestId = fields[1];
                var until = DateTime.UtcNow.AddSeconds(10);
                NexIrcDraft? draft = null;
                while (DateTime.UtcNow < until
                    && (!window.ViewModel.Sessions.TryGetSynchronizedDraftState(activeChannel, out draft)
                        || draft is null || draft.Revision < revision))
                    await Task.Delay(20).ConfigureAwait(true);
                Emit($"DESKTOP_WAIT_REVISION request={requestId} outcome={(draft is not null && draft.Revision >= revision ? "Ready" : "Timeout")} revision={draft?.Revision.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            }
            else if (command.Equals("DISCONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await window.ViewModel.Sessions.DisconnectAsync(workspace.Id).ConfigureAwait(true);
                Emit("DESKTOP_DISCONNECTED");
            }
            else if (command.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
            {
                await window.CloseAfterSmokeAsync().ConfigureAwait(true);
                Emit("DESKTOP_EXITING");
                return 0;
            }
            else
            {
                Emit("DESKTOP_COMMAND_RESULT outcome=Unknown");
            }
        }

        await window.CloseAfterSmokeAsync().ConfigureAwait(true);
        return 0;
    }

    private static void EmitComposerState(MainWindow window, NetworkWorkspace workspace, WorkspaceView view, string requestId)
    {
        var conversationKey = NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot);
        var persisted = window.ConversationDraftStore.Load().FirstOrDefault(draft => draft.ProfileId == workspace.ProfileId
            && string.Equals(draft.ConversationKey, conversationKey, StringComparison.Ordinal));
        var hasAuthority = window.ViewModel.Sessions.TryGetSynchronizedDraftState(view, out var authority);
        var input = window.ViewModel.InputText;
        var local = persisted?.Text ?? string.Empty;
        var localServer = persisted?.ServerText ?? string.Empty;
        var pendingText = persisted?.PendingText ?? string.Empty;
        var authorityText = hasAuthority ? authority?.Text ?? string.Empty : string.Empty;
        Emit($"DESKTOP_COMPOSER_STATE request={requestId} inputBytes={Encoding.UTF8.GetByteCount(input).ToString(CultureInfo.InvariantCulture)} inputSha256={Fingerprint(input)} localBytes={Encoding.UTF8.GetByteCount(local).ToString(CultureInfo.InvariantCulture)} localSha256={Fingerprint(local)} baseRevision={persisted?.BaseRevision.ToString(CultureInfo.InvariantCulture) ?? "none"} conflict={persisted?.HasConflict.ToString().ToLowerInvariant() ?? "false"} localServerRevision={persisted?.ServerRevision.ToString(CultureInfo.InvariantCulture) ?? "none"} localServerBytes={Encoding.UTF8.GetByteCount(localServer).ToString(CultureInfo.InvariantCulture)} localServerSha256={Fingerprint(localServer)} pendingMutationId={persisted?.PendingMutationId ?? "none"} pendingBaseRevision={persisted?.PendingBaseRevision?.ToString(CultureInfo.InvariantCulture) ?? "none"} pendingTextBytes={Encoding.UTF8.GetByteCount(pendingText).ToString(CultureInfo.InvariantCulture)} pendingTextSha256={Fingerprint(pendingText)} authorityRevision={(hasAuthority ? authority?.Revision.ToString(CultureInfo.InvariantCulture) : "none")} authorityBytes={Encoding.UTF8.GetByteCount(authorityText).ToString(CultureInfo.InvariantCulture)} authoritySha256={Fingerprint(authorityText)}");
    }

    private static ChannelView FindChannel(NetworkWorkspace workspace, string channelName) =>
        workspace.Channels.FirstOrDefault(channel => channel.Channel.Equals(channelName.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("The requested channel view was not part of the desktop fixture workspace.");

    private static bool IsVersionEnabled(CapabilitySnapshot capabilities, string name, string version) =>
        capabilities.IsEnabled(name) && capabilities.Available.TryGetValue(name, out var capability)
        && string.Equals(capability.Value, version, StringComparison.Ordinal);

    private static EventWaitHandle? OpenBarrier(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Named process barriers require Windows.");
        return EventWaitHandle.OpenExisting(name);
    }

    private static void EmitPinSnapshot(NexIrcPinStateSnapshot snapshot)
    {
        var active = snapshot.Entries.Where(static item => item.IsPinned).OrderByDescending(static item => item.OrderSequence).ToArray();
        Emit($"DESKTOP_PIN_SNAPSHOT generation={snapshot.Generation.ToString(CultureInfo.InvariantCulture)} revision={snapshot.CatalogRevision.ToString(CultureInfo.InvariantCulture)} entries={snapshot.Entries.Count.ToString(CultureInfo.InvariantCulture)} active={active.Length.ToString(CultureInfo.InvariantCulture)} order={string.Join(',', active.Select(static item => Fingerprint(item.ConversationKey)[..16]))}");
    }

    private static void EmitPinCatalog(MainWindow window, NetworkWorkspace workspace, string requestId)
    {
        if (!window.ViewModel.Sessions.TryGetSynchronizedPinCatalog(workspace, out var snapshot) || snapshot is null)
        {
            Emit($"DESKTOP_PIN_CATALOG request={requestId} outcome=Unavailable");
            return;
        }
        Emit($"DESKTOP_PIN_CATALOG request={requestId} outcome=Ready generation={snapshot.Generation.ToString(CultureInfo.InvariantCulture)} revision={snapshot.CatalogRevision.ToString(CultureInfo.InvariantCulture)} entries={snapshot.Entries.Count.ToString(CultureInfo.InvariantCulture)} active={snapshot.Entries.Count(static item => item.IsPinned).ToString(CultureInfo.InvariantCulture)} order={string.Join(',', snapshot.Entries.Where(static item => item.IsPinned).OrderByDescending(static item => item.OrderSequence).Select(static item => Fingerprint(item.ConversationKey)[..16]))} tracked={string.Join(',', snapshot.Entries.OrderBy(static item => item.ConversationKey, StringComparer.Ordinal).Select(static item => $"{Fingerprint(item.ConversationKey)[..16]}:{item.Revision.ToString(CultureInfo.InvariantCulture)}:{(item.IsPinned ? 1 : 0)}:{item.OrderSequence.ToString(CultureInfo.InvariantCulture)}"))}");
    }

    private static void EmitPinProjection(MainWindow window, string requestId)
    {
        var rows = window.ViewModel.PinnedConversations.ToArray();
        var fingerprints = rows.Select(static item => Fingerprint(NetworkSessionManager.SynchronizedPinConversationKey(item.View, item.Network.Snapshot) ?? string.Empty)[..16]).ToArray();
        Emit($"DESKTOP_PIN_PROJECTION request={requestId} rows={rows.Length.ToString(CultureInfo.InvariantCulture)} unique={fingerprints.Distinct(StringComparer.Ordinal).Count().ToString(CultureInfo.InvariantCulture)} order={string.Join(',', fingerprints)}");
    }

    private static void EmitPinWorkspace(MainWindow window, NetworkWorkspace workspace, string requestId)
    {
        var channels = workspace.Channels.OrderBy(static channel => channel.Channel, StringComparer.OrdinalIgnoreCase).Select(channel =>
            $"{Fingerprint(channel.Channel)[..16]}:{channel.EntriesSnapshot.Count.ToString(CultureInfo.InvariantCulture)}:{channel.UnreadCount.ToString(CultureInfo.InvariantCulture)}:{channel.ImportantCount.ToString(CultureInfo.InvariantCulture)}");
        Emit($"DESKTOP_PIN_WORKSPACE request={requestId} views={workspace.Views.Count.ToString(CultureInfo.InvariantCulture)} channels={workspace.Channels.Count.ToString(CultureInfo.InvariantCulture)} state={string.Join(',', channels)} composerBytes={Encoding.UTF8.GetByteCount(window.ViewModel.InputText).ToString(CultureInfo.InvariantCulture)} composerHash={Fingerprint(window.ViewModel.InputText)}");
    }

    private static async Task ObserveConnectTaskAsync(Task connectTask)
    {
        try
        {
            await connectTask.ConfigureAwait(false);
            Emit("DESKTOP_CONNECT_TASK outcome=Completed");
        }
        catch (Exception exception)
        {
            Emit($"DESKTOP_CONNECT_TASK outcome=Failed type={exception.GetType().Name}");
        }
    }

    private static string CommandRequestId(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? Guid.NewGuid().ToString("N");

    private static async Task WritePairingMaterialAsync(string pipeName, string material)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new InvalidOperationException("The private pairing-material channel was unavailable.");
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true);
        await writer.WriteLineAsync(material.AsMemory(), timeout.Token).ConfigureAwait(true);
        await writer.FlushAsync(timeout.Token).ConfigureAwait(true);
    }

    private static string Decode(string encoded)
    {
        var normalized = encoded.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Emit(string message)
    {
        Console.Out.WriteLine(message);
        Console.Out.Flush();
    }

    private sealed class FixtureCredentialProvider(string username, string password) : ISaslCredentialProvider
    {
        public ValueTask<SaslCredential?> GetCredentialsAsync(
            IrcEndpoint endpoint,
            string mechanism,
            CancellationToken cancellationToken = default)
        {
            _ = endpoint;
            _ = mechanism;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SaslCredential?>(new SaslCredential(username, password));
        }
    }

    private sealed record Options(
        string Host,
        int Port,
        string CertificateThumbprint,
        string DraftStorePath,
        string ResumeStorePath,
        Guid ProfileId,
        string Username,
        string Nickname,
        IReadOnlyList<string> Channels,
        string SecretPipeName,
        string PinBeforeSendBarrierName,
        bool CreateAttachment,
        bool ResumeExistingAttachment)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index].Equals(Flag, StringComparison.OrdinalIgnoreCase)) continue;
                if (index + 1 < args.Length) values[args[index]] = args[++index];
            }

            string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException("A required Phase 53 desktop fixture option was missing.");

            return new Options(
                Required("--host"),
                int.Parse(Required("--port"), CultureInfo.InvariantCulture),
                Required("--thumbprint"),
                Path.GetFullPath(Required("--draft-store")),
                Path.GetFullPath(Required("--resume-store")),
                Guid.Parse(Required("--profile-id")),
                Required("--username"),
                Required("--nickname"),
                (values.GetValueOrDefault("--channels") ?? values.GetValueOrDefault("--channel") ?? "#general")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                values.GetValueOrDefault("--secret-pipe") ?? string.Empty,
                values.GetValueOrDefault("--pin-before-send-barrier") ?? string.Empty,
                string.Equals(values.GetValueOrDefault("--create-attachment"), "true", StringComparison.OrdinalIgnoreCase),
                string.Equals(values.GetValueOrDefault("--resume-existing"), "true", StringComparison.OrdinalIgnoreCase));
        }
    }
}
