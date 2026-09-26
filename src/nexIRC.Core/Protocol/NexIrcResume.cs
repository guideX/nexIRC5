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
    public const string CapabilityVersion = "1";
    public const string Command = "NEXIRC";
    public const string SessionSubcommand = "SESSION";
    public const string ResumeSubcommand = "RESUME";
    public const string BatchType = "nexirc/resume";
    public const string ResumeSequenceTag = "resume-seq";
    public const string ResumePreviousSequenceTag = "resume-prev";
    public const int MaximumOpaqueValueLength = 256;

    public static bool IsSupported(CapabilitySnapshot capabilities) =>
        capabilities.IsEnabled(CapabilityName)
        && capabilities.Available.TryGetValue(CapabilityName, out var capability)
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
    public NexIrcResumeSession(string token, string authoritativeBoundary, int establishedGeneration)
    {
        if (!NexIrcResumeProtocol.IsSafeOpaqueValue(token))
        {
            throw new ArgumentException("The resume token is not a safe opaque IRC value.", nameof(token));
        }

        if (!NexIrcResumeProtocol.IsSafeOpaqueValue(authoritativeBoundary))
        {
            throw new ArgumentException("The resume boundary is not a safe opaque IRC value.", nameof(authoritativeBoundary));
        }

        Token = token;
        AuthoritativeBoundary = authoritativeBoundary;
        EstablishedGeneration = establishedGeneration;
    }

    /// <summary>Opaque protocol material; never include this property in diagnostics.</summary>
    public string Token { get; }

    public string TokenFingerprint => NexIrcResumeProtocol.FingerprintToken(Token);

    public string AuthoritativeBoundary { get; }

    public int EstablishedGeneration { get; }

    public NexIrcResumeSession Advance(string authoritativeBoundary) =>
        new(Token, authoritativeBoundary, EstablishedGeneration);

    public NexIrcResumeSession Rotate(string token, string authoritativeBoundary, int generation) =>
        new(token, authoritativeBoundary, generation);

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
}
