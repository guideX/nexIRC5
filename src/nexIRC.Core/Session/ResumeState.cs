namespace nexIRC.Core.Session;

using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;

/// <summary>
/// The only client-side durable resume format currently understood by nexIRC.
/// The token fields contain protected bytes, never bearer-token text.
/// </summary>
public sealed record ClientResumeStateRecord(
    int Version,
    string NetworkIdentity,
    string AccountIdentity,
    string ProtocolVersion,
    byte[] ProtectedCurrentToken,
    byte[]? ProtectedPendingToken,
    int TokenGeneration,
    int? PendingTokenGeneration,
    int AcknowledgedTokenGeneration,
    string AuthoritativeBoundary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt = null,
    string? ServerGeneration = null,
    Guid? AttachmentId = null,
    byte[]? ProtectedSessionCredential = null,
    byte[]? ProtectedPairingRecoveryCredential = null,
    string? PairingRecoveryBoundary = null,
    bool PairingRecoveryCredentialsDurable = false)
{
    public const int CurrentVersion = 3;
    public const int MaximumProtectedTokenBytes = 16 * 1024;
    public const int MaximumProtectedSessionCredentialBytes = 16 * 1024;
    public const int MaximumProtectedPairingRecoveryCredentialBytes = 16 * 1024;

    public bool IsSupported => Version == CurrentVersion;

    public bool IsWellFormed =>
        Version > 0
        && !string.IsNullOrWhiteSpace(NetworkIdentity)
        && !string.IsNullOrWhiteSpace(AccountIdentity)
        && string.Equals(ProtocolVersion, "1", StringComparison.Ordinal)
        && ProtectedCurrentToken is { Length: > 0 and <= MaximumProtectedTokenBytes }
        && TokenGeneration > 0
        && AcknowledgedTokenGeneration > 0
        && AcknowledgedTokenGeneration <= TokenGeneration
        && !string.IsNullOrWhiteSpace(AuthoritativeBoundary)
        && (AttachmentId is null || AttachmentId != Guid.Empty)
        && (ProtectedSessionCredential is null
            || ProtectedSessionCredential.Length is > 0 and <= MaximumProtectedSessionCredentialBytes)
        && (ProtectedPairingRecoveryCredential is null
            ? PairingRecoveryBoundary is null && !PairingRecoveryCredentialsDurable
            : ProtectedPairingRecoveryCredential.Length is > 0 and <= MaximumProtectedPairingRecoveryCredentialBytes
                && !string.IsNullOrWhiteSpace(PairingRecoveryBoundary)
                && NexIrcResumeProtocol.IsSafeOpaqueValue(PairingRecoveryBoundary))
        && (ProtectedPendingToken is null
            ? PendingTokenGeneration is null
            : ProtectedPendingToken.Length is > 0 and <= MaximumProtectedTokenBytes
                && PendingTokenGeneration is { } pendingGeneration
                && pendingGeneration > 0
                && pendingGeneration > TokenGeneration);
}

public enum ResumeStateLoadStatus
{
    Missing,
    Loaded,
    Corrupt,
    Unsupported,
    Unavailable
}

public sealed record ResumeStateLoadResult(
    ResumeStateLoadStatus Status,
    ClientResumeStateRecord? State = null,
    string? Detail = null)
{
    public bool IsUsable => Status == ResumeStateLoadStatus.Loaded && State is not null;

    public static ResumeStateLoadResult Missing { get; } = new(ResumeStateLoadStatus.Missing);
}

public enum ResumeStateStoreStatus
{
    Stored,
    Deleted,
    Missing,
    Unavailable,
    Failed,
    Corrupt
}

public sealed record ResumeStateStoreResult(
    ResumeStateStoreStatus Status,
    string? Detail = null)
{
    public bool Succeeded => Status is ResumeStateStoreStatus.Stored or ResumeStateStoreStatus.Deleted;
}

/// <summary>
/// Protects a short-lived bearer secret with a platform-owned mechanism.
/// Implementations must not write the plaintext secret themselves.
/// </summary>
public interface IResumeSecretProtector
{
    byte[] Protect(string secret, string protectionContext);

    string Unprotect(ReadOnlySpan<byte> protectedSecret, string protectionContext);
}

/// <summary>
/// Stores bounded, versioned resume metadata. Secret protection is deliberately
/// supplied separately so a platform vault can replace the file implementation.
/// </summary>
public interface IResumeStateStore
{
    ValueTask<ResumeStateLoadResult> LoadAsync(
        string networkIdentity,
        CancellationToken cancellationToken = default);

    ValueTask<ResumeStateStoreResult> SaveAsync(
        ClientResumeStateRecord state,
        CancellationToken cancellationToken = default);

    ValueTask<ResumeStateStoreResult> DeleteAsync(
        string networkIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Test and embedded-host store. It intentionally holds only the protected
/// representation, making tests exercise the same no-plaintext boundary as the
/// desktop store.
/// </summary>
public sealed class InMemoryResumeStateStore : IResumeStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ClientResumeStateRecord> _states = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public ValueTask<ResumeStateLoadResult> LoadAsync(string networkIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return ValueTask.FromResult(new ResumeStateLoadResult(ResumeStateLoadStatus.Unavailable, Detail: "The resume-state store is unavailable."));
        }

        lock (_gate)
        {
            return ValueTask.FromResult(_states.TryGetValue(networkIdentity, out var state)
                ? new ResumeStateLoadResult(ResumeStateLoadStatus.Loaded, state)
                : ResumeStateLoadResult.Missing);
        }
    }

    public ValueTask<ResumeStateStoreResult> SaveAsync(ClientResumeStateRecord state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return ValueTask.FromResult(new ResumeStateStoreResult(ResumeStateStoreStatus.Unavailable, "The resume-state store is unavailable."));
        }

        lock (_gate)
        {
            _states[state.NetworkIdentity] = state with
            {
                ProtectedCurrentToken = state.ProtectedCurrentToken.ToArray(),
                ProtectedPendingToken = state.ProtectedPendingToken?.ToArray(),
                ProtectedSessionCredential = state.ProtectedSessionCredential?.ToArray(),
                ProtectedPairingRecoveryCredential = state.ProtectedPairingRecoveryCredential?.ToArray()
            };
        }

        return ValueTask.FromResult(new ResumeStateStoreResult(ResumeStateStoreStatus.Stored));
    }

    public ValueTask<ResumeStateStoreResult> DeleteAsync(string networkIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return ValueTask.FromResult(new ResumeStateStoreResult(ResumeStateStoreStatus.Unavailable, "The resume-state store is unavailable."));
        }

        lock (_gate)
        {
            return ValueTask.FromResult(new ResumeStateStoreResult(
                _states.Remove(networkIdentity) ? ResumeStateStoreStatus.Deleted : ResumeStateStoreStatus.Missing));
        }
    }
}

/// <summary>
/// Deterministic, non-secret binding used as additional entropy for DPAPI and
/// as the store lookup key. It includes the profile/network scope and endpoint
/// policy, not a socket or process identifier.
/// </summary>
public static class ResumeStateIdentity
{
    public static string For(IrcEndpoint endpoint, Guid? networkId)
    {
        var scope = networkId is Guid id && id != Guid.Empty ? id.ToString("N") : "unscoped";
        return $"profile={scope};host={endpoint.Host.Trim().ToLowerInvariant()};port={endpoint.Port};tls={endpoint.UseTls.ToString().ToLowerInvariant()}";
    }
}
