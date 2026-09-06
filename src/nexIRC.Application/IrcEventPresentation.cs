using nexIRC.Core.Session;

namespace nexIRC.Application;

public static class IrcEventPresentation
{
    public static TranscriptEntry? Render(IrcSemanticEvent semanticEvent, ServerSessionSnapshot snapshot) =>
        semanticEvent switch
        {
            IrcWelcomeEvent welcome => Entry(TranscriptEntryKind.Connection, null, welcome.NetworkName is null ? "Connected; server identity is not known yet." : $"Connected to {welcome.NetworkName}."),
            IrcRegistrationStateEvent registration => Entry(TranscriptEntryKind.Registration, null, $"Registration: {registration.Previous} → {registration.Current}."),
            IrcCapabilityChangedEvent capability => Entry(TranscriptEntryKind.Capability, null, $"CAP {capability.Kind}: {string.Join(' ', capability.Capabilities)}"),
            IrcSaslStateChangedEvent sasl => Entry(sasl.Current == SaslAuthenticationState.Failed ? TranscriptEntryKind.Error : TranscriptEntryKind.Authentication, null, $"SASL: {sasl.Current}{FormatDetail(sasl.Detail)}"),
            IrcServerErrorEvent error => Entry(TranscriptEntryKind.Error, null, error.Text),
            IrcJoinEvent join => Entry(TranscriptEntryKind.Join, join.Nickname, $"joined {join.Channel}"),
            IrcPartEvent part => Entry(TranscriptEntryKind.Part, part.Nickname, $"left {part.Channel}"),
            IrcQuitEvent quit => Entry(TranscriptEntryKind.Quit, quit.Nickname, $"quit{FormatDetail(quit.Reason)}"),
            IrcKickEvent kick => Entry(TranscriptEntryKind.Kick, kick.Nickname, $"was kicked from {kick.Channel}{FormatDetail(kick.Reason)}"),
            IrcNicknameChangedEvent nick => Entry(TranscriptEntryKind.Nick, nick.PreviousNickname, $"is now known as {nick.NewNickname}"),
            IrcCtcpEvent ctcp when ctcp.Command == "ACTION" => Entry(TranscriptEntryKind.Action, ctcp.Message.Prefix?.Name, ctcp.Arguments),
            IrcCtcpEvent ctcp => Entry(TranscriptEntryKind.Ctcp, ctcp.Message.Prefix?.Name, FormatCtcp(ctcp.Command, ctcp.Arguments)),
            IrcPrivmsgEvent message when message.IsNotice => Entry(TranscriptEntryKind.Notice, message.Message.Prefix?.Name, message.Text),
            IrcPrivmsgEvent message => Entry(TranscriptEntryKind.Message, message.Message.Prefix?.Name, message.Text),
            IrcQueryMessageEvent query when query.IsNotice => Entry(TranscriptEntryKind.Notice, query.Nickname, query.Text),
            IrcQueryMessageEvent query => Entry(TranscriptEntryKind.Message, query.Nickname, query.Text),
            IrcTopicEvent topic => Entry(TranscriptEntryKind.Topic, topic.Message.Prefix?.Name, $"set topic in {topic.Channel}: {topic.Topic}"),
            IrcTopicUnsetEvent topic => Entry(TranscriptEntryKind.Topic, topic.Message.Prefix?.Name, $"cleared the topic in {topic.Channel}"),
            IrcNamesEvent names => Entry(TranscriptEntryKind.Informational, null, $"NAMES {names.Channel}: {names.Nicknames.Count} member(s) observed."),
            IrcNamesCompleteEvent names => Entry(TranscriptEntryKind.Informational, null, $"Member list synchronized for {names.Channel}."),
            IrcModeEvent mode => Entry(TranscriptEntryKind.Mode, mode.Message.Prefix?.Name, $"changed modes in {mode.Channel}: {FormatModes(mode.Changes)}"),
            IrcServerNumericEvent numeric => Entry(
                numeric.IsError ? TranscriptEntryKind.Error : TranscriptEntryKind.Informational,
                null,
                $"{numeric.Interpretation.FriendlyExplanation} [{numeric.Name} {numeric.Numeric}: {numeric.Interpretation.ProtocolText}]"),
            IrcBanListEndEvent end => Entry(TranscriptEntryKind.List, null, $"Ban list complete for {end.Channel}."),
            IrcBanListItemEvent => null,
            IrcChannelSynchronizationEvent synchronization => Entry(TranscriptEntryKind.Informational, null, $"{synchronization.Channel}: {synchronization.State}."),
            IrcMotdEvent motd => Entry(TranscriptEntryKind.Motd, null, motd.Kind == IrcMotdEventKind.Line ? motd.Text : $"MOTD {motd.Kind.ToString().ToLowerInvariant()}: {motd.Text}"),
            IrcListStartEvent => Entry(TranscriptEntryKind.List, null, "Channel list started."),
            IrcListItemEvent item => Entry(TranscriptEntryKind.List, null, $"LIST {item.Channel}: {item.VisibleUsers} user(s), {item.Topic}"),
            IrcListEndEvent => Entry(TranscriptEntryKind.List, null, "Channel list complete."),
            IrcWhoEvent who => Entry(TranscriptEntryKind.Informational, null, $"WHO {who.Channel}: {who.Nickname} ({who.Username}@{who.Host})"),
            IrcWhoEndEvent who => Entry(TranscriptEntryKind.Informational, null, $"WHO {who.Target}: {who.Text}"),
            IrcWhoisEvent whois => Entry(TranscriptEntryKind.Whois, null, $"WHOIS {whois.Nickname} ({whois.Numeric}): {whois.Text ?? string.Join(' ', whois.Parameters)}"),
            IrcUnknownNumericEvent unknown => Entry(TranscriptEntryKind.Numeric, null, $"Unknown server numeric {unknown.Numeric}: {MessageText(unknown.Message)}"),
            IrcUnknownCommandEvent unknown => Entry(TranscriptEntryKind.Numeric, null, $"Unknown server command {unknown.Message.Command}: {MessageText(unknown.Message)}"),
            IrcNumericEvent numeric when numeric.Numeric is 332 or 331 or 324 or 353 or 366 or 375 or 372 or 376 or 422 => null,
            IrcNumericEvent numeric => Entry(TranscriptEntryKind.Informational, null, $"Server reply {numeric.Numeric}: {MessageText(numeric.Message)}"),
            IrcPingEvent => null,
            _ => Entry(TranscriptEntryKind.System, null, semanticEvent.Message.Command)
        };

    public static TranscriptEntry CreateLocalMessage(string sender, string text, bool isAction = false) =>
        CreateLocalMessage(sender, text, isAction ? OutgoingMessageKind.Action : OutgoingMessageKind.ChannelMessage);

    public static TranscriptEntry CreateLocalMessage(string sender, string text, OutgoingMessageKind kind) =>
        Entry(kind switch
        {
            OutgoingMessageKind.PrivateMessage => TranscriptEntryKind.OutgoingPrivateMessage,
            OutgoingMessageKind.Action => TranscriptEntryKind.OutgoingAction,
            OutgoingMessageKind.Notice => TranscriptEntryKind.OutgoingNotice,
            _ => TranscriptEntryKind.OutgoingMessage
        }, sender, text);

    public static TranscriptEntry CreateLocalCommand(string text) => Entry(TranscriptEntryKind.System, null, text);

    public static TranscriptEntry CreateLocalCtcp(string command, string arguments) =>
        Entry(TranscriptEntryKind.OutgoingCtcp, null, FormatCtcp(command, arguments));

    private static TranscriptEntry Entry(TranscriptEntryKind kind, string? sender, string text, string? metadata = null) =>
        new(DateTimeOffset.UtcNow, kind, sender, text, metadata);

    private static string FormatDetail(string? detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";

    private static string FormatCtcp(string command, string arguments) => string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}";

    private static string FormatModes(IReadOnlyList<nexIRC.Core.State.IrcModeChange> changes) => string.Join(' ', changes.Select(change =>
        $"{(change.IsAdding ? '+' : '-')}{change.Mode}{(change.Parameter is null ? string.Empty : $" {change.Parameter}")}"));

    private static string MessageText(nexIRC.Core.Protocol.IrcMessage message) => message.HasTrailingParameter
        ? message.TrailingParameter ?? string.Empty
        : string.Join(' ', message.Parameters);
}

public enum OutgoingMessageKind
{
    ChannelMessage,
    PrivateMessage,
    Action,
    Notice
}
