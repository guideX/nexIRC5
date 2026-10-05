using System.Globalization;
using System.IO;
using System.IO.Pipes;
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

        var options = Options.Parse(args);
        var password = await Console.In.ReadLineAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Missing SASL fixture credential input.");

        var protector = new WindowsDpapiResumeSecretProtector();
        using var resumeStore = new JsonResumeStateStore(options.ResumeStorePath);
        var draftStore = new ProtectedJsonConversationDraftStore(options.DraftStorePath, protector);
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
        window.Show();

        var requestedCapabilities = IrcCapabilityCatalog.PreferredPhase1Y;
        var workspace = window.ViewModel.Sessions.Add(new NetworkConnectionOptions
        {
            ProfileId = options.ProfileId,
            DisplayName = "Phase 53 desktop process fixture",
            Endpoint = new IrcEndpoint(options.Host, options.Port, useTls: true),
            Nickname = options.Nickname,
            Username = options.Username,
            RealName = "nexIRC Phase 53 desktop process fixture",
            DesiredChannels = new HashSet<string>([options.Channel], StringComparer.OrdinalIgnoreCase),
            RequestedCapabilities = requestedCapabilities,
            SaslPolicy = SaslAuthenticationPolicy.Required,
            SaslCredentialProvider = new FixtureCredentialProvider(options.Username, password),
            ResumeNetworkIdentity = $"phase53-process-profile:{options.ProfileId:N}",
            CreateNewAttachmentOnConnect = options.CreateAttachment,
            ResumeExistingAttachmentOnConnect = options.ResumeExistingAttachment,
            Reconnect = new ReconnectPolicy(Enabled: false)
        });
        var managerSnapshotApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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

        workspace.Session.OutboundCommandSent += (_, outbound) =>
        {
            if (outbound.RawLine.StartsWith($"PRIVMSG {options.Channel} :", StringComparison.OrdinalIgnoreCase))
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
        await window.ViewModel.Sessions.ConnectAsync(workspace.Id).ConfigureAwait(true);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (workspace.Session.Snapshot.Registration == RegistrationState.Registered
                && workspace.Session.NativeResumeSession is not null
                && (!options.CreateAttachment || workspace.Session.NativeResumeSession.AttachmentId is not null)
                && (!options.ResumeExistingAttachment || workspace.Session.Continuity.State == ConnectionContinuityState.Synchronized)
                && workspace.Channels.Any(channel => channel.Channel.Equals(options.Channel, StringComparison.OrdinalIgnoreCase))
                && workspace.Session.Snapshot.State is ServerSessionState.Connected or ServerSessionState.Registered)
                break;
            if (workspace.Session.Snapshot.State == ServerSessionState.Failed)
                throw new InvalidOperationException($"The desktop process fixture session failed ({workspace.Session.Snapshot.LastFailure?.Kind.ToString() ?? "unknown"}).");
            await Task.Delay(25).ConfigureAwait(true);
        }

        var activeChannel = workspace.Channels.FirstOrDefault(channel => channel.Channel.Equals(options.Channel, StringComparison.OrdinalIgnoreCase));
        if (activeChannel is null || workspace.Session.Snapshot.State is not (ServerSessionState.Connected or ServerSessionState.Registered))
            throw new TimeoutException($"The desktop process fixture did not reach readiness (state={workspace.Session.Snapshot.State}, registration={workspace.Session.Snapshot.Registration}, attachment={workspace.Session.NativeResumeSession?.AttachmentId is not null}, continuity={workspace.Session.Continuity.State}, channels={workspace.Channels.Count}).");

        await managerSnapshotApplied.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        window.ViewModel.SelectView(activeChannel);
        await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        var managerHasDraftSnapshot = window.ViewModel.Sessions.TryGetSynchronizedDraftState(activeChannel, out var managerDraft);
        var diagnostics = window.ViewModel.Sessions.Diagnostics;
        if (!managerHasDraftSnapshot)
            throw new InvalidOperationException("The desktop Application did not apply its initial synchronized draft snapshot.");
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
        Emit($"DESKTOP_COMPOSER_STATE request={requestId} input64={Encode(window.ViewModel.InputText)} local64={Encode(persisted?.Text ?? string.Empty)} baseRevision={persisted?.BaseRevision.ToString(CultureInfo.InvariantCulture) ?? "none"} conflict={persisted?.HasConflict.ToString().ToLowerInvariant() ?? "false"} localServerRevision={persisted?.ServerRevision.ToString(CultureInfo.InvariantCulture) ?? "none"} localServer64={Encode(persisted?.ServerText ?? string.Empty)} pendingMutationId={persisted?.PendingMutationId ?? "none"} pendingBaseRevision={persisted?.PendingBaseRevision?.ToString(CultureInfo.InvariantCulture) ?? "none"} pendingText64={Encode(persisted?.PendingText ?? string.Empty)} authorityRevision={(hasAuthority ? authority?.Revision.ToString(CultureInfo.InvariantCulture) : "none")} authority64={Encode(hasAuthority ? authority?.Text ?? string.Empty : string.Empty)}");
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

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
        string Channel,
        string SecretPipeName,
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
                values.GetValueOrDefault("--channel") ?? "#general",
                values.GetValueOrDefault("--secret-pipe") ?? string.Empty,
                string.Equals(values.GetValueOrDefault("--create-attachment"), "true", StringComparison.OrdinalIgnoreCase),
                string.Equals(values.GetValueOrDefault("--resume-existing"), "true", StringComparison.OrdinalIgnoreCase));
        }
    }
}
