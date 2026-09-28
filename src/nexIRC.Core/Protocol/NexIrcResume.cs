using System.Security.Cryptography;
using System.Text;
using nexIRC.Core.State;

namespace nexIRC.Core.Protocol;

/// <summary>
/// Experimental, IRC-compatible nexIRC session-resume protocol constants and
/// value objects. The client treats both the resume token and replay boundary
/// as opaque strings; only the explicit predecessor links in the wire stream
/// establish ordering.
/// </summary>
public static class NexIrcResumeProtocol
{
    public const string CapabilityName = IrcCapabilityCatalog.NexIrcResume;
    public const string AttachmentsCapabilityName = IrcCapabilityCatalog.NexIrcAttachments;
    public const string CapabilityVersion = "1";
    public const string Command = "NEXIRC";
    public const string SessionSubcommand = "SESSION";
    public const string SessionRotateSubcommand = "ROTATE";
    public const string SessionAckSubcommand = "ACK";
    public const string ResumeSubcommand = "RESUME";
    public const string AttachSubcommand = "ATTACH";
    public const string PairSubcommand = "PAIR";
    public const string PairCreateSubcommand = "CREATE";
    public const string PairUseSubcommand = "USE";
    public const string PairRecoverSubcommand = "RECOVER";
    public const string PairCustodySubcommand = "CUSTODY";
    public const string PairRevokeSubcommand = "REVOKE";
    public const string BatchType = "nexirc/resume";
    public const string ResumeSequenceTag = "resume-seq";
    public const string ResumePreviousSequenceTag = "resume-prev";
    public const int MaximumOpaqueValueLength = 256;

    public static bool IsSupported(CapabilitySnapshot capabilities) =>
        capabilities.IsEnabled(CapabilityName)
        && capabilities.Available.TryGetValue(CapabilityName, out var capability)
        && string.Equals(capability.Value ?? CapabilityVersion, CapabilityVersion, StringComparison.Ordinal);

    public static bool AreAttachmentsSupported(CapabilitySnapshot capabilities) =>
        IsSupported(capabilities)
        && capabilities.IsEnabled(AttachmentsCapabilityName)
        && capabilities.Available.TryGetValue(AttachmentsCapabilityName, out var capability)
        && string.Equals(capability.Value ?? CapabilityVersion, CapabilityVersion, StringComparison.Ordinal);

    public static string FingerprintToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static bool IsSafeOpaqueValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumOpaqueValueLength
        && !value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character) || character == ':');

    public static string CreatePairingMaterial(string code, string boundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        if (!IsSafeOpaqueValue(code) || !IsSafeOpaqueValue(boundary))
        {
            throw new ArgumentException("The pairing authorization is malformed.", nameof(code));
        }

        return $"npair1.{code}.{boundary}";
    }

    public static string CreatePairingRecoveryCredential()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return "phr1_" + Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool TryParsePairingMaterial(
        string? material,
        string? networkIdentity,
        out string code,
        out string boundary)
    {
        code = string.Empty;
        boundary = string.Empty;
        if (string.IsNullOrWhiteSpace(material)
            || material.Length > MaximumOpaqueValueLength
            || string.IsNullOrWhiteSpace(networkIdentity))
        {
            return false;
        }

        var parts = material.Split('.', 3, StringSplitOptions.None);
        if (parts.Length != 3
            || !string.Equals(parts[0], "npair1", StringComparison.Ordinal)
            || !IsSafeOpaqueValue(parts[1])
            || !IsSafeOpaqueValue(parts[2]))
        {
            return false;
        }

        var networkFingerprint = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(networkIdentity)).AsSpan(0, 12))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        if (!parts[1].StartsWith($"np1_{networkFingerprint}_", StringComparison.Ordinal))
        {
            return false;
        }

        code = parts[1];
        boundary = parts[2];
        return true;
    }

    public static bool TryReadOpaqueParameters(
        IrcMessage message,
        int startIndex,
        out string first,
        out string second)
    {
        first = string.Empty;
        second = string.Empty;
        if (message.Parameters.Count <= startIndex + 1)
        {
            return false;
        }

        var candidateFirst = message.Parameters[startIndex];
        var candidateSecond = message.Parameters[startIndex + 1];
        if (!IsSafeOpaqueValue(candidateFirst) || !IsSafeOpaqueValue(candidateSecond))
        {
            return false;
        }

        first = candidateFirst;
        second = candidateSecond;
        return true;
    }
}

/// <summary>
/// One-time, user-visible authorization to add a device. The material must be
/// shown only in the explicit pairing surface and must never be logged.
/// </summary>
public sealed record NexIrcPairingAuthorization(string Material, DateTimeOffset ExpiresAt)
{
    public string Fingerprint => NexIrcResumeProtocol.FingerprintToken(Material);

    public override string ToString() => $"pairing={Fingerprint}; expires={ExpiresAt:O}";
}

public sealed record NexIrcResumeSupport(
    bool CapabilityEnabled,
    bool SessionAvailable,
    string? Version)
{
    public bool IsUsable => CapabilityEnabled && SessionAvailable
        && (Version is null || string.Equals(Version, NexIrcResumeProtocol.CapabilityVersion, StringComparison.Ordinal));

    public static NexIrcResumeSupport Unavailable { get; } = new(false, false, null);
}

public sealed class NexIrcResumeSession
{
    public NexIrcResumeSession(
        string token,
        string authoritativeBoundary,
        int establishedGeneration,
        string? previousToken = null,
        int? previousGeneration = null,
        Guid? attachmentId = null,
        string? sessionCredential = null)
    {
        if (!NexIrcResumeProtocol.IsSafeOpaqueValue(token))
        {
            throw new ArgumentException("The resume token is not a safe opaque IRC value.", nameof(token));
        }

        if (!NexIrcResumeProtocol.IsSafeOpaqueValue(authoritativeBoundary))
        {
            throw new ArgumentException("The resume boundary is not a safe opaque IRC value.", nameof(authoritativeBoundary));
        }

        if (previousToken is not null && !NexIrcResumeProtocol.IsSafeOpaqueValue(previousToken))
        {
            throw new ArgumentException("The previous resume token is not a safe opaque IRC value.", nameof(previousToken));
        }

        if (previousToken is null && previousGeneration is not null
            || previousToken is not null && previousGeneration is not > 0)
        {
            throw new ArgumentException("A previous resume token and generation must be supplied together.", nameof(previousGeneration));
        }

        if (attachmentId == Guid.Empty)
        {
            throw new ArgumentException("An attachment ID must be a non-empty GUID when supplied.", nameof(attachmentId));
        }

        if (sessionCredential is not null && !NexIrcResumeProtocol.IsSafeOpaqueValue(sessionCredential))
        {
            throw new ArgumentException("The session attachment credential is not a safe opaque IRC value.", nameof(sessionCredential));
        }

        Token = token;
        AuthoritativeBoundary = authoritativeBoundary;
        EstablishedGeneration = establishedGeneration;
        PreviousToken = previousToken;
        PreviousGeneration = previousGeneration;
        AttachmentId = attachmentId;
        SessionCredential = sessionCredential;
    }

    /// <summary>Opaque protocol material; never include this property in diagnostics.</summary>
    public string Token { get; }

    public string TokenFingerprint => NexIrcResumeProtocol.FingerprintToken(Token);

    public string AuthoritativeBoundary { get; }

    public int EstablishedGeneration { get; }

    /// <summary>
    /// The server's prior current token while a replacement is awaiting the
    /// client acknowledgement. The active <see cref="Token"/> is the
    /// replacement and is safe to offer after a restart because the server's
    /// bounded overlap accepts it.
    /// </summary>
    public string? PreviousToken { get; }

    public int? PreviousGeneration { get; }

    public Guid? AttachmentId { get; }

    /// <summary>Session-scoped grant used only to create another attachment; never log this value.</summary>
    public string? SessionCredential { get; }

    public bool HasPendingRotation => PreviousToken is not null;

    public string DurableCurrentToken => PreviousToken ?? Token;

    public int DurableCurrentGeneration => PreviousGeneration ?? EstablishedGeneration;

    public NexIrcResumeSession Advance(string authoritativeBoundary) =>
        new(Token, authoritativeBoundary, EstablishedGeneration, PreviousToken, PreviousGeneration, AttachmentId, SessionCredential);

    public NexIrcResumeSession Rotate(string token, string authoritativeBoundary, int generation) =>
        new(token, authoritativeBoundary, generation, Token, EstablishedGeneration, AttachmentId, SessionCredential);

    public NexIrcResumeSession MarkRotationAcknowledged() =>
        new(Token, AuthoritativeBoundary, EstablishedGeneration, attachmentId: AttachmentId, sessionCredential: SessionCredential);

    public NexIrcResumeSession WithAttachment(Guid attachmentId) =>
        new(Token, AuthoritativeBoundary, EstablishedGeneration, PreviousToken, PreviousGeneration, attachmentId, SessionCredential);

    public NexIrcResumeSession WithSessionCredential(string sessionCredential) =>
        new(Token, AuthoritativeBoundary, EstablishedGeneration, PreviousToken, PreviousGeneration, AttachmentId, sessionCredential);

    public static NexIrcResumeSession FromDurableState(
        string currentToken,
        string? pendingToken,
        string authoritativeBoundary,
        int tokenGeneration,
        int? pendingTokenGeneration,
        Guid? attachmentId = null,
        string? sessionCredential = null) =>
        pendingToken is { Length: > 0 } pending && pendingTokenGeneration is { } pendingGeneration
            ? new(pending, authoritativeBoundary, pendingGeneration, currentToken, tokenGeneration, attachmentId, sessionCredential)
            : new(currentToken, authoritativeBoundary, tokenGeneration, attachmentId: attachmentId, sessionCredential: sessionCredential);

    public override string ToString() =>
        $"session={TokenFingerprint}; boundary={AuthoritativeBoundary}; generation={EstablishedGeneration}";
}

public enum NexIrcResumeOutcome
{
    Unsupported,
    Rejected,
    Accepted,
    Completed,
    Failed,
    Cancelled
}

public enum NexIrcResumeRejectionReason
{
    UnknownToken,
    ExpiredToken,
    AccountMismatch,
    BoundaryTooOld,
    SessionInvalidated,
    ServerRestarted,
    ReplayTooLarge,
    RateLimited,
    AttachmentLimit,
    AuthenticationRequired,
    TemporaryFailure,
    Unsupported,
    NewSessionRequired,
    Malformed
}

public sealed record NexIrcResumeExecutionResult(
    NexIrcResumeOutcome Outcome,
    bool CapabilityNegotiated,
    bool RequestSent,
    bool ReplayAccepted,
    bool ReplayCompleted,
    bool ExactBoundaryRecovered,
    int ReplayedEventCount,
    int DuplicateEventsSuppressed,
    string? RequestedBoundary,
    string? FinalBoundary,
    string? Detail,
    NexIrcResumeRejectionReason? RejectionReason,
    bool FallbackSafe)
{
    public bool AttachmentCreated { get; init; }

    public static NexIrcResumeExecutionResult Unsupported(string detail) => new(
        NexIrcResumeOutcome.Unsupported,
        CapabilityNegotiated: false,
        RequestSent: false,
        ReplayAccepted: false,
        ReplayCompleted: false,
        ExactBoundaryRecovered: false,
        ReplayedEventCount: 0,
        DuplicateEventsSuppressed: 0,
        RequestedBoundary: null,
        FinalBoundary: null,
        detail,
        NexIrcResumeRejectionReason.Unsupported,
        FallbackSafe: true);

    public static NexIrcResumeExecutionResult Failed(string detail) => new(
        NexIrcResumeOutcome.Failed,
        CapabilityNegotiated: true,
        RequestSent: false,
        ReplayAccepted: false,
        ReplayCompleted: false,
        ExactBoundaryRecovered: false,
        ReplayedEventCount: 0,
        DuplicateEventsSuppressed: 0,
        RequestedBoundary: null,
        FinalBoundary: null,
        detail,
        NexIrcResumeRejectionReason.TemporaryFailure,
        FallbackSafe: false);
}
