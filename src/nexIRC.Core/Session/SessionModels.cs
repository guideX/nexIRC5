using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Session;

public enum ServerSessionState
{
    Disconnected,
    Connecting,
    TlsNegotiation,
    Connected,
    CapNegotiation,
    Registering,
    Registered,
    Disconnecting,
    ReconnectWaiting,
    Failed
}

public enum RegistrationState
{
    NotStarted,
    CapNegotiating,
    Registering,
    Registered,
    Failed
}

public enum SaslAuthenticationPolicy
{
    Disabled,
    Optional,
    Preferred,
    Required
}

public enum SaslAuthenticationState
{
    Disabled,
    WaitingForCapability,
    Negotiating,
    Succeeded,
    Skipped,
    Failed
}

public sealed record SaslAuthenticationSnapshot(
    SaslAuthenticationPolicy Policy,
    SaslAuthenticationState State,
    string? Mechanism,
    string? Failure,
    int ConnectionGeneration)
{
    public static SaslAuthenticationSnapshot Disabled { get; } = new(
        SaslAuthenticationPolicy.Disabled,
        SaslAuthenticationState.Disabled,
        null,
        null,
        0);
}

public sealed record ReconnectPolicy(
    bool Enabled = true,
    int MaximumAttempts = 3,
    TimeSpan? InitialDelay = null,
    TimeSpan? MaximumDelay = null)
{
    public TimeSpan EffectiveInitialDelay => InitialDelay ?? TimeSpan.FromMilliseconds(250);

    public TimeSpan EffectiveMaximumDelay => MaximumDelay ?? TimeSpan.FromSeconds(30);
}

public sealed class ServerSessionOptions
{
    public Guid? NetworkId { get; init; }

    public required IrcEndpoint Endpoint { get; init; }

    public required string Nickname { get; init; }

    public string Username { get; init; } = "nexirc";

    public string RealName { get; init; } = "nexIRC 5";

    public string? Password { get; init; }

    public IServerPasswordProvider? PasswordProvider { get; init; }

    public string? AlternateNickname { get; init; }

    /// <summary>
    /// Ordered fallback nicknames tried after a registration-time collision.
    /// The first configured Nickname remains the desired primary identity.
    /// </summary>
    public IReadOnlyList<string> NicknameFallbacks { get; init; } = Array.Empty<string>();

    public int MaximumInboundLineBytes { get; init; } = IrcLineFramer.DefaultMaximumLineBytes;

    public int MaximumOutboundLineBytes { get; init; } = IrcCommandBuilder.DefaultMaximumLineBytes;

    public int MaximumChathistoryRequestSize { get; init; } = ChathistorySupport.DefaultClientMaximumRequestSize;

    public TimeSpan ChathistoryRequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int ReadBufferBytes { get; init; } = 4096;

    public IReadOnlyList<string> RequestedCapabilities { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Bounds the pre-registration wait for servers that ignore CAP entirely.
    /// A server that does not answer within this window is treated as a
    /// legacy CAP-less server and registration continues without extensions.
    /// </summary>
    public TimeSpan CapabilityNegotiationTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public IReadOnlySet<string> DesiredChannels { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public ReconnectPolicy Reconnect { get; init; } = new();

    public ServerProfileSet? Profiles { get; init; }

    public string? ManualNetworkName { get; init; }

    public IrcdFamily? ManualIrcd { get; init; }

    public ISaslCredentialProvider? SaslCredentialProvider { get; init; }

    public IReadOnlyList<ISaslMechanism> SaslMechanisms { get; init; } = [new SaslPlainMechanism()];

    public SaslAuthenticationPolicy SaslPolicy { get; init; } = SaslAuthenticationPolicy.Disabled;
}

public interface IServerPasswordProvider
{
    ValueTask<string?> GetPasswordAsync(IrcEndpoint endpoint, CancellationToken cancellationToken = default);
}

public sealed record RawIrcLineEvent(
    DateTimeOffset ReceivedAt,
    string RawLine,
    ReadOnlyMemory<byte> RawBytes,
    int ConnectionGeneration);

public sealed record ParsedIrcMessageEvent(
    DateTimeOffset ReceivedAt,
    IrcMessage Message,
    int ConnectionGeneration);

public sealed record IrcParseErrorEvent(
    DateTimeOffset ReceivedAt,
    string RawLine,
    string Error,
    int ConnectionGeneration);

public abstract record IrcSemanticEvent(IrcMessage Message)
{
    /// <summary>
    /// True only for content accepted from a validated server history batch.
    /// Historical messages can share the normal typed message events while
    /// remaining outside the live participant/activity state machine.
    /// </summary>
    public bool IsHistorical { get; init; }

    /// <summary>Delivery provenance independent of the application history schema.</summary>
    public IrcSemanticEventSource Source { get; init; } = IrcSemanticEventSource.Live;

    /// <summary>Network scope carried onto server-playback events.</summary>
    public Guid? NetworkId { get; init; }

    /// <summary>Logical conversation supplied by the owning history request.</summary>
    public string? HistoricalConversation { get; init; }
}

public enum IrcSemanticEventSource
{
    Live,
    ServerPlayback,
    DiscoveryMetadata
}

public sealed record IrcWelcomeEvent(IrcMessage Message, string? NetworkName) : IrcSemanticEvent(Message);

public sealed record IrcPingEvent(IrcMessage Message, string Payload) : IrcSemanticEvent(Message);

public sealed record IrcServerErrorEvent(IrcMessage Message, string Text) : IrcSemanticEvent(Message);

public sealed record IrcRegistrationStateEvent(
    IrcMessage Message,
    RegistrationState Previous,
    RegistrationState Current) : IrcSemanticEvent(Message);

public enum IrcCapabilityChangeKind
{
    Available,
    Removed,
    Enabled,
    Disabled,
    Rejected
}

public sealed record IrcCapabilityChangedEvent(
    IrcMessage Message,
    IrcCapabilityChangeKind Kind,
    IReadOnlyList<string> Capabilities) : IrcSemanticEvent(Message);

public sealed record IrcSaslStateChangedEvent(
    IrcMessage Message,
    SaslAuthenticationState Previous,
    SaslAuthenticationState Current,
    string? Mechanism,
    string? Detail) : IrcSemanticEvent(Message);

public sealed record IrcChannelSynchronizationEvent(
    IrcMessage Message,
    string Channel,
    ChannelSynchronizationState State) : IrcSemanticEvent(Message);

public sealed record IrcNicknameChangedEvent(IrcMessage Message, string? PreviousNickname, string NewNickname) : IrcSemanticEvent(Message);

public sealed record IrcJoinEvent(
    IrcMessage Message,
    string Channel,
    string Nickname,
    string? Account = null,
    string? RealName = null) : IrcSemanticEvent(Message);

public sealed record IrcAwayEvent(
    IrcMessage Message,
    string Nickname,
    bool IsAway,
    string? Reason) : IrcSemanticEvent(Message);

public sealed record IrcAccountEvent(
    IrcMessage Message,
    string Nickname,
    string? Account) : IrcSemanticEvent(Message);

public sealed record IrcBatchEvent(
    IrcMessage Message,
    string BatchId,
    bool IsStart,
    string? Type,
    IReadOnlyList<string> Parameters) : IrcSemanticEvent(Message);

public sealed record IrcHistoryTargetEvent(
    IrcMessage Message,
    string Target,
    DateTimeOffset LatestTimestamp,
    ChathistoryTargetKind Kind) : IrcSemanticEvent(Message);

public sealed record IrcTagmsgEvent(
    IrcMessage Message,
    string Target) : IrcSemanticEvent(Message);

public enum IrcStandardReplyKind
{
    Fail,
    Warn,
    Note
}

public sealed record IrcStandardReplyEvent(
    IrcMessage Message,
    IrcStandardReplyKind Kind,
    string Code,
    string Text) : IrcSemanticEvent(Message);

public sealed record IrcPartEvent(IrcMessage Message, string Channel, string Nickname) : IrcSemanticEvent(Message);

public sealed record IrcQuitEvent(IrcMessage Message, string Nickname, string? Reason) : IrcSemanticEvent(Message);

public sealed record IrcPrivmsgEvent(IrcMessage Message, string Target, string Text, bool IsNotice) : IrcSemanticEvent(Message);

public sealed record IrcCtcpEvent(
    IrcMessage Message,
    string Target,
    string Command,
    string Arguments,
    bool IsNotice) : IrcSemanticEvent(Message);

public sealed record IrcTopicEvent(
    IrcMessage Message,
    string Channel,
    string Topic,
    string? Setter = null,
    DateTimeOffset? SetAt = null) : IrcSemanticEvent(Message);

public sealed record IrcTopicMetadataEvent(
    IrcMessage Message,
    string Channel,
    string? Setter,
    DateTimeOffset? SetAt) : IrcSemanticEvent(Message);

public sealed record IrcNamesEvent(IrcMessage Message, string Channel, IReadOnlyList<string> Nicknames) : IrcSemanticEvent(Message);

public sealed record IrcNamesCompleteEvent(IrcMessage Message, string Channel) : IrcSemanticEvent(Message);

public sealed record IrcModeEvent(
    IrcMessage Message,
    string Channel,
    IReadOnlyList<IrcModeChange> Changes) : IrcSemanticEvent(Message);

public sealed record IrcTopicUnsetEvent(IrcMessage Message, string Channel) : IrcSemanticEvent(Message);

public sealed record IrcMotdEvent(
    IrcMessage Message,
    IrcMotdEventKind Kind,
    string Text) : IrcSemanticEvent(Message);

public enum IrcMotdEventKind
{
    Start,
    Line,
    End
}

public sealed record IrcListStartEvent(IrcMessage Message, string? RequestLabel = null) : IrcSemanticEvent(Message);

public sealed record IrcListItemEvent(
    IrcMessage Message,
    string Channel,
    int VisibleUsers,
    string Topic,
    string? RequestLabel = null) : IrcSemanticEvent(Message);

public sealed record IrcListEndEvent(IrcMessage Message, string? RequestLabel = null) : IrcSemanticEvent(Message);

public sealed record IrcWhoEvent(
    IrcMessage Message,
    string Channel,
    string Nickname,
    string Username,
    string Host,
    string Server,
    string Status,
    string RealName) : IrcSemanticEvent(Message);

public sealed record IrcWhoEndEvent(IrcMessage Message, string Target, string Text) : IrcSemanticEvent(Message);

public sealed record IrcWhoisEvent(
    IrcMessage Message,
    int Numeric,
    string Nickname,
    IReadOnlyList<string> Parameters,
    string? Text,
    string? RequestLabel = null) : IrcSemanticEvent(Message);

public sealed record IrcServerNumericEvent(
    IrcMessage Message,
    IrcNumericInterpretation Interpretation) : IrcSemanticEvent(Message)
{
    public int Numeric => Interpretation.Numeric;

    public string Name => Interpretation.Name;

    public bool IsError => Interpretation.IsError;

    public bool IsSuccess => Interpretation.IsSuccess;
}

public sealed record IrcBanListEntry(
    string Channel,
    string Mask,
    string? Setter,
    DateTimeOffset? SetAt);

public sealed record IrcBanListItemEvent(
    IrcMessage Message,
    IrcBanListEntry Entry,
    string? RequestLabel = null) : IrcSemanticEvent(Message);

public sealed record IrcBanListEndEvent(
    IrcMessage Message,
    string Channel,
    string? RequestLabel = null) : IrcSemanticEvent(Message);

public sealed record IrcNumericEvent(IrcMessage Message, int Numeric) : IrcSemanticEvent(Message);

public sealed record IrcUnknownCommandEvent(IrcMessage Message) : IrcSemanticEvent(Message);

public sealed record IrcUnknownNumericEvent(IrcMessage Message, int Numeric) : IrcSemanticEvent(Message);

public sealed record IrcQueryMessageEvent(IrcMessage Message, string Nickname, string Text, bool IsNotice) : IrcSemanticEvent(Message);

public sealed record IrcKickEvent(IrcMessage Message, string Channel, string Nickname, string? Reason) : IrcSemanticEvent(Message);

public sealed record IrcChannelMemberSnapshot(
    string Nickname,
    string? Username,
    string? Host,
    IReadOnlySet<char> PrefixModes,
    string? Account = null,
    string? RealName = null,
    bool IsAway = false,
    string? AwayReason = null);

public sealed record IrcChannelSnapshot(
    string Name,
    bool IsJoined,
    bool IsStale,
    string? Topic,
    IReadOnlyDictionary<string, IrcChannelMemberSnapshot> Members,
    int ConnectionGeneration)
{
    public string? TopicSetter { get; init; }

    public DateTimeOffset? TopicSetAt { get; init; }

    public IReadOnlySet<char> Modes { get; init; } = new HashSet<char>();

    public IReadOnlyDictionary<char, IReadOnlyList<string>> ModeParameters { get; init; } =
        new Dictionary<char, IReadOnlyList<string>>();

    public IReadOnlyDictionary<char, IReadOnlyList<string>> ModeArguments => ModeParameters;

    public ChannelSynchronizationState Synchronization { get; init; } = ChannelSynchronizationState.NotRequested;
}

public enum ChannelSynchronizationState
{
    NotRequested,
    Joining,
    Synchronizing,
    Synchronized
}

public sealed record IrcMotdSnapshot(
    bool IsComplete,
    IReadOnlyList<string> Lines,
    int ConnectionGeneration)
{
    public static IrcMotdSnapshot Empty { get; } = new(false, Array.Empty<string>(), 0);
}

public sealed record IrcQuerySnapshot(
    string Nickname,
    IReadOnlyList<string> Messages,
    int ConnectionGeneration);

public sealed record ServerSessionSnapshot(
    ServerSessionState State,
    RegistrationState Registration,
    string Nickname,
    string Username,
    string RealName,
    IrcEndpoint Endpoint,
    int ConnectionGeneration,
    CapabilitySnapshot Capabilities,
    ISupportSnapshot ISupport,
    ServerIdentity Identity,
    ServerFeatureSet Features,
    ConnectionFailure? LastFailure,
    IReadOnlyList<IrcChannelSnapshot> Channels,
    IReadOnlyList<IrcQuerySnapshot> Queries,
    IReadOnlySet<string> DesiredChannels)
{
    public IrcMotdSnapshot Motd { get; init; } = IrcMotdSnapshot.Empty;

    public SaslAuthenticationSnapshot Authentication { get; init; } = SaslAuthenticationSnapshot.Disabled;

    public string DesiredNickname { get; init; } = string.Empty;
}

public sealed record OutboundIrcCommandEvent(
    DateTimeOffset SentAt,
    string RawLine,
    ReadOnlyMemory<byte> RawBytes,
    int ConnectionGeneration);

public sealed record SessionStateChangedEvent(
    ServerSessionState Previous,
    ServerSessionState Current,
    int ConnectionGeneration);

public sealed record SessionSemanticEvent(
    IrcSemanticEvent Event,
    int ConnectionGeneration,
    DateTimeOffset? ReceivedAt = null);
