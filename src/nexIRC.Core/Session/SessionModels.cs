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
    public required IrcEndpoint Endpoint { get; init; }

    public required string Nickname { get; init; }

    public string Username { get; init; } = "nexirc";

    public string RealName { get; init; } = "nexIRC 5";

    public string? Password { get; init; }

    public string? AlternateNickname { get; init; }

    public int MaximumInboundLineBytes { get; init; } = IrcLineFramer.DefaultMaximumLineBytes;

    public int MaximumOutboundLineBytes { get; init; } = IrcCommandBuilder.DefaultMaximumLineBytes;

    public int ReadBufferBytes { get; init; } = 4096;

    public IReadOnlyList<string> RequestedCapabilities { get; init; } = Array.Empty<string>();

    public IReadOnlySet<string> DesiredChannels { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public ReconnectPolicy Reconnect { get; init; } = new();

    public ServerProfileSet? Profiles { get; init; }

    public string? ManualNetworkName { get; init; }

    public IrcdFamily? ManualIrcd { get; init; }
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

public abstract record IrcSemanticEvent(IrcMessage Message);

public sealed record IrcWelcomeEvent(IrcMessage Message, string? NetworkName) : IrcSemanticEvent(Message);

public sealed record IrcPingEvent(IrcMessage Message, string Payload) : IrcSemanticEvent(Message);

public sealed record IrcServerErrorEvent(IrcMessage Message, string Text) : IrcSemanticEvent(Message);

public sealed record IrcNicknameChangedEvent(IrcMessage Message, string? PreviousNickname, string NewNickname) : IrcSemanticEvent(Message);

public sealed record IrcJoinEvent(IrcMessage Message, string Channel, string Nickname) : IrcSemanticEvent(Message);

public sealed record IrcPartEvent(IrcMessage Message, string Channel, string Nickname) : IrcSemanticEvent(Message);

public sealed record IrcQuitEvent(IrcMessage Message, string Nickname, string? Reason) : IrcSemanticEvent(Message);

public sealed record IrcPrivmsgEvent(IrcMessage Message, string Target, string Text, bool IsNotice) : IrcSemanticEvent(Message);

public sealed record IrcTopicEvent(IrcMessage Message, string Channel, string Topic) : IrcSemanticEvent(Message);

public sealed record IrcNamesEvent(IrcMessage Message, string Channel, IReadOnlyList<string> Nicknames) : IrcSemanticEvent(Message);

public sealed record IrcNumericEvent(IrcMessage Message, int Numeric) : IrcSemanticEvent(Message);

public sealed record IrcUnknownCommandEvent(IrcMessage Message) : IrcSemanticEvent(Message);

public sealed record IrcUnknownNumericEvent(IrcMessage Message, int Numeric) : IrcSemanticEvent(Message);

public sealed record IrcQueryMessageEvent(IrcMessage Message, string Nickname, string Text, bool IsNotice) : IrcSemanticEvent(Message);

public sealed record IrcChannelMemberSnapshot(
    string Nickname,
    string? Username,
    string? Host,
    IReadOnlySet<char> PrefixModes);

public sealed record IrcChannelSnapshot(
    string Name,
    bool IsJoined,
    bool IsStale,
    string? Topic,
    IReadOnlyDictionary<string, IrcChannelMemberSnapshot> Members,
    int ConnectionGeneration);

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
    IReadOnlySet<string> DesiredChannels);

public sealed record SessionStateChangedEvent(
    ServerSessionState Previous,
    ServerSessionState Current,
    int ConnectionGeneration);

public sealed record SessionSemanticEvent(IrcSemanticEvent Event, int ConnectionGeneration);
