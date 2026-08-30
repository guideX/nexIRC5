using System.Security.Cryptography;
using nexIRC.Core.Networking;
using nexIRC.Core.State;

namespace nexIRC.Desktop;

/// <summary>
/// Holds the connection-dialog secret only in memory and creates a fresh
/// disposable Phase 1B credential for each authentication exchange.
/// </summary>
public sealed class MemorySaslCredentialProvider : ISaslCredentialProvider, IDisposable
{
    private readonly string _userName;
    private char[]? _password;

    public MemorySaslCredentialProvider(string userName, string password)
    {
        _userName = string.IsNullOrWhiteSpace(userName) ? throw new ArgumentException("A SASL user name is required.", nameof(userName)) : userName;
        ArgumentNullException.ThrowIfNull(password);
        _password = password.ToCharArray();
    }

    public ValueTask<SaslCredential?> GetCredentialsAsync(IrcEndpoint endpoint, string mechanism, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var password = _password ?? throw new ObjectDisposedException(nameof(MemorySaslCredentialProvider));
        var copy = new string(password);
        try
        {
            return ValueTask.FromResult<SaslCredential?>(new SaslCredential(_userName, copy));
        }
        finally
        {
            copy = string.Empty;
        }
    }

    public void Dispose()
    {
        if (_password is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_password.AsSpan()));
        _password = null;
    }
}
