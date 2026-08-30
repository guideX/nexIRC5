using System.Security.Cryptography;
using System.Text;
using nexIRC.Core.Networking;

namespace nexIRC.Core.State;

/// <summary>
/// A short-lived SASL credential owned by the host that supplied it. The
/// session does not retain, log, serialize, or put this value in a snapshot.
/// </summary>
public sealed class SaslCredential : IDisposable
{
    private char[]? _secret;

    public SaslCredential(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("A SASL user name is required.", nameof(userName));
        }

        ArgumentNullException.ThrowIfNull(password);
        if (userName.Contains('\0') || password.Contains('\0'))
        {
            throw new ArgumentException("SASL credentials cannot contain a NUL character.");
        }

        UserName = userName;
        _secret = password.ToCharArray();
    }

    public string UserName { get; }

    public bool IsDisposed => _secret is null;

    public int SecretLength => _secret?.Length ?? 0;

    public int CopySecret(Span<char> destination)
    {
        var secret = _secret ?? throw new ObjectDisposedException(nameof(SaslCredential));
        if (destination.Length < secret.Length)
        {
            throw new ArgumentException("The destination is too small for the SASL secret.", nameof(destination));
        }

        secret.AsSpan().CopyTo(destination);
        return secret.Length;
    }

    public void Dispose()
    {
        if (_secret is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_secret.AsSpan()));
        _secret = null;
    }
}

/// <summary>
/// The application/host remains the authority for retrieving credentials.
/// Implementations should prompt or use a platform credential vault and return
/// a disposable credential only for the active authentication exchange.
/// </summary>
public interface ISaslCredentialProvider
{
    ValueTask<SaslCredential?> GetCredentialsAsync(
        IrcEndpoint endpoint,
        string mechanism,
        CancellationToken cancellationToken = default);
}

public interface ISaslMechanism
{
    string Name { get; }

    bool SupportsInitialResponse { get; }

    ValueTask<ReadOnlyMemory<byte>> CreateInitialResponseAsync(
        SaslCredential credential,
        CancellationToken cancellationToken = default);

    ValueTask<ReadOnlyMemory<byte>> CreateChallengeResponseAsync(
        SaslCredential credential,
        ReadOnlyMemory<byte> challenge,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SASL PLAIN is exposed as a mechanism implementation, but ServerSession does
/// not select or send it automatically. A future authentication coordinator
/// must explicitly opt in and must dispose the host-provided credential.
/// </summary>
public sealed class SaslPlainMechanism : ISaslMechanism
{
    public string Name => "PLAIN";

    public bool SupportsInitialResponse => true;

    public ValueTask<ReadOnlyMemory<byte>> CreateInitialResponseAsync(
        SaslCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();

        var userNameBytes = Encoding.UTF8.GetByteCount(credential.UserName);
        var secretBuffer = new char[credential.SecretLength];
        try
        {
            var secretLength = credential.CopySecret(secretBuffer);
            var secretBytes = Encoding.UTF8.GetByteCount(secretBuffer.AsSpan(0, secretLength));
            var response = new byte[1 + userNameBytes + 1 + secretBytes];
            var offset = 1;
            offset += Encoding.UTF8.GetBytes(credential.UserName, response.AsSpan(offset));
            response[offset++] = 0;
            Encoding.UTF8.GetBytes(secretBuffer.AsSpan(0, secretLength), response.AsSpan(offset));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(response);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(secretBuffer.AsSpan()));
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> CreateChallengeResponseAsync(
        SaslCredential credential,
        ReadOnlyMemory<byte> challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("SASL PLAIN does not define a challenge response.");
    }
}

public sealed class SaslMechanismRegistry
{
    private readonly Dictionary<string, ISaslMechanism> _mechanisms = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ISaslMechanism> Mechanisms => _mechanisms.Values.ToArray();

    public void Register(ISaslMechanism mechanism)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        if (string.IsNullOrWhiteSpace(mechanism.Name))
        {
            throw new ArgumentException("A SASL mechanism requires a name.", nameof(mechanism));
        }

        _mechanisms[mechanism.Name] = mechanism;
    }

    public bool TryGet(string name, out ISaslMechanism? mechanism)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _mechanisms.TryGetValue(name, out mechanism);
    }
}
