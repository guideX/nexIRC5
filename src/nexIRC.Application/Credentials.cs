using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum ProfileCredentialKind
{
    Sasl,
    ServerPassword,
    NickServ
}

public enum CredentialStoreStatus
{
    Stored,
    Missing,
    Deleted,
    Unavailable,
    AccessDenied,
    Corrupt,
    Failed
}

public sealed record CredentialOperationResult(CredentialStoreStatus Status, string? Diagnostic = null)
{
    public bool Succeeded => Status is CredentialStoreStatus.Stored or CredentialStoreStatus.Deleted;
}

public sealed record CredentialLoadResult(
    CredentialStoreStatus Status,
    SaslCredential? Credential = null,
    string? Diagnostic = null)
{
    public bool Exists => Status == CredentialStoreStatus.Stored && Credential is not null;
}

/// <summary>
/// Profile-scoped secret storage. Implementations own the protected storage;
/// callers receive a disposable credential only for the active exchange.
/// </summary>
public interface IProfileCredentialStore
{
    ValueTask<CredentialLoadResult> LoadAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default);

    ValueTask<bool> ExistsAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default);

    ValueTask<CredentialOperationResult> SaveAsync(Guid profileId, ProfileCredentialKind kind, string userName, string password, CancellationToken cancellationToken = default);

    ValueTask<CredentialOperationResult> DeleteAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default);

    ValueTask<CredentialOperationResult> ClearProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Application-level credential facade used by WPF. A storage failure is
/// represented as a status/diagnostic and never falls back to plaintext.
/// </summary>
public sealed class ProfileCredentialService
{
    public ProfileCredentialService(IProfileCredentialStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public IProfileCredentialStore Store { get; }

    public string? LastDiagnostic { get; private set; }

    public async ValueTask<CredentialLoadResult> LoadAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Store.LoadAsync(profileId, kind, cancellationToken).ConfigureAwait(false);
            LastDiagnostic = result.Diagnostic;
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            LastDiagnostic = "Secure credential storage failed safely; no plaintext fallback was used.";
            return new CredentialLoadResult(CredentialStoreStatus.Unavailable, Diagnostic: LastDiagnostic);
        }
    }

    public async ValueTask<bool> ExistsAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await Store.ExistsAsync(profileId, kind, cancellationToken).ConfigureAwait(false);
            LastDiagnostic = null;
            return exists;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            LastDiagnostic = "Secure credential storage is unavailable; no secret was written insecurely.";
            return false;
        }
    }

    public async ValueTask<CredentialOperationResult> SaveAsync(Guid profileId, ProfileCredentialKind kind, string userName, string password, CancellationToken cancellationToken = default)
    {
        if (password is null || userName is null)
        {
            return new CredentialOperationResult(CredentialStoreStatus.Corrupt, "A credential username and password are required.");
        }

        try
        {
            var result = await Store.SaveAsync(profileId, kind, userName, password, cancellationToken).ConfigureAwait(false);
            LastDiagnostic = result.Diagnostic;
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            LastDiagnostic = "Secure credential storage failed safely; the password was not written insecurely.";
            return new CredentialOperationResult(CredentialStoreStatus.Unavailable, LastDiagnostic);
        }
    }

    public async ValueTask<CredentialOperationResult> DeleteAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Store.DeleteAsync(profileId, kind, cancellationToken).ConfigureAwait(false);
            LastDiagnostic = result.Diagnostic;
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            LastDiagnostic = "Secure credential deletion failed safely.";
            return new CredentialOperationResult(CredentialStoreStatus.Unavailable, LastDiagnostic);
        }
    }

    public async ValueTask<CredentialOperationResult> ClearProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Store.ClearProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            LastDiagnostic = result.Diagnostic;
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            LastDiagnostic = "Secure credential cleanup failed safely.";
            return new CredentialOperationResult(CredentialStoreStatus.Unavailable, LastDiagnostic);
        }
    }
}

/// <summary>
/// Bridges the secure profile store to the existing Core SASL contract.
/// </summary>
public sealed class ProfileSaslCredentialProvider(Guid profileId, IProfileCredentialStore store) : ISaslCredentialProvider
{
    public async ValueTask<SaslCredential?> GetCredentialsAsync(IrcEndpoint endpoint, string mechanism, CancellationToken cancellationToken = default)
    {
        var result = await store.LoadAsync(profileId, ProfileCredentialKind.Sasl, cancellationToken).ConfigureAwait(false);
        if (result.Status != CredentialStoreStatus.Stored)
        {
            return null;
        }

        return result.Credential;
    }
}

public sealed class ProfileServerPasswordProvider(Guid profileId, IProfileCredentialStore store) : IServerPasswordProvider
{
    public async ValueTask<string?> GetPasswordAsync(IrcEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        var result = await store.LoadAsync(profileId, ProfileCredentialKind.ServerPassword, cancellationToken).ConfigureAwait(false);
        if (result.Status != CredentialStoreStatus.Stored || result.Credential is null)
        {
            return null;
        }

        using var credential = result.Credential;
        var secret = new char[credential.SecretLength];
        try
        {
            var length = credential.CopySecret(secret);
            return new string(secret, 0, length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(secret.AsSpan()));
        }
    }
}

/// <summary>
/// Deterministic fake for tests and --demo. It deliberately keeps values only
/// in process memory and is never used by the desktop production composition.
/// </summary>
public sealed class InMemoryProfileCredentialStore : IProfileCredentialStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid ProfileId, ProfileCredentialKind Kind), (string UserName, string Password)> _values = [];

    public bool IsAvailable { get; set; } = true;

    public ValueTask<CredentialLoadResult> LoadAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return ValueTask.FromResult(new CredentialLoadResult(CredentialStoreStatus.Unavailable, Diagnostic: "The test credential provider is unavailable."));
        }

        lock (_gate)
        {
            if (!_values.TryGetValue((profileId, kind), out var value))
            {
                return ValueTask.FromResult(new CredentialLoadResult(CredentialStoreStatus.Missing));
            }

            return ValueTask.FromResult(new CredentialLoadResult(CredentialStoreStatus.Stored, new SaslCredential(value.UserName, value.Password)));
        }
    }

    public ValueTask<bool> ExistsAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return ValueTask.FromResult(false);
        }

        lock (_gate)
        {
            return ValueTask.FromResult(_values.ContainsKey((profileId, kind)));
        }
    }

    public ValueTask<CredentialOperationResult> SaveAsync(Guid profileId, ProfileCredentialKind kind, string userName, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Unavailable, "The test credential provider is unavailable; the password was not saved."));
        }

        if (profileId == Guid.Empty || string.IsNullOrWhiteSpace(userName) || password.Length > 512 || userName.Length > 256 || userName.Contains('\0') || password.Contains('\0'))
        {
            return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Corrupt, "The credential did not meet the supported bounds."));
        }

        lock (_gate)
        {
            _values[(profileId, kind)] = (userName, password);
        }

        return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Stored));
    }

    public ValueTask<CredentialOperationResult> DeleteAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Unavailable, "The test credential provider is unavailable; the password was not deleted."));
        }

        lock (_gate)
        {
            _values.Remove((profileId, kind));
        }

        return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Deleted));
    }

    public ValueTask<CredentialOperationResult> ClearProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Unavailable, "The test credential provider is unavailable; credentials were not deleted."));
        }

        lock (_gate)
        {
            foreach (var key in _values.Keys.Where(key => key.ProfileId == profileId).ToArray())
            {
                _values.Remove(key);
            }
        }

        return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Deleted));
    }
}

/// <summary>
/// Windows Credential Manager implementation. The secret is passed to the OS
/// vault as an opaque generic credential and is never serialized by nexIRC.
/// </summary>
public sealed class WindowsCredentialStore : IProfileCredentialStore
{
    private const int ErrorNotFound = 1168;
    private const int ErrorAccessDenied = 5;
    private const int MaximumBlobBytes = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ValueTask<CredentialLoadResult> LoadAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!CredRead(Target(profileId, kind), CredentialTypeGeneric, 0, out var credentialPointer))
            {
                return ValueTask.FromResult(new CredentialLoadResult(StatusFromLastError(), Diagnostic: DiagnosticFromLastError("Credential Manager could not read the credential.")));
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize is <= 0 or > MaximumBlobBytes || credential.UserNameText is null)
                {
                    return ValueTask.FromResult(new CredentialLoadResult(CredentialStoreStatus.Corrupt, Diagnostic: "Credential Manager returned a corrupt protected payload."));
                }

                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                try
                {
                    var password = StrictUtf8.GetString(bytes);
                    return ValueTask.FromResult(new CredentialLoadResult(CredentialStoreStatus.Stored, new SaslCredential(credential.UserNameText, password)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            finally
            {
                CredFree(credentialPointer);
            }
        }
        catch (Exception exception) when (IsStorageException(exception) || exception is ArgumentException)
        {
            return ValueTask.FromResult(new CredentialLoadResult(
                exception is ArgumentException ? CredentialStoreStatus.Corrupt : CredentialStoreStatus.Unavailable,
                Diagnostic: exception is ArgumentException ? "Credential Manager returned an invalid protected payload." : "Windows Credential Manager is unavailable; no plaintext fallback was used."));
        }
    }

    public async ValueTask<bool> ExistsAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        var result = await LoadAsync(profileId, kind, cancellationToken).ConfigureAwait(false);
        result.Credential?.Dispose();
        return result.Status == CredentialStoreStatus.Stored;
    }

    public ValueTask<CredentialOperationResult> SaveAsync(Guid profileId, ProfileCredentialKind kind, string userName, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(password);
        var bytes = Encoding.UTF8.GetBytes(password);
        if (profileId == Guid.Empty || string.IsNullOrWhiteSpace(userName) || userName.Length > 256 || bytes.Length > MaximumBlobBytes || userName.Contains('\0') || password.Contains('\0'))
        {
            return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Corrupt, "The credential exceeded the Windows Credential Manager bounds."));
        }

        var target = Target(profileId, kind);
        var userPointer = IntPtr.Zero;
        var blobPointer = IntPtr.Zero;
        try
        {
            userPointer = Marshal.StringToCoTaskMemUni(userName);
            blobPointer = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, blobPointer, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = Marshal.StringToCoTaskMemUni(target),
                UserNamePointer = userPointer,
                CredentialBlob = blobPointer,
                CredentialBlobSize = bytes.Length,
                Persist = CredentialPersistLocalMachine
            };
            try
            {
                return CredWrite(ref credential, 0)
                    ? ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Stored))
                    : ValueTask.FromResult(new CredentialOperationResult(StatusFromLastError(), DiagnosticFromLastError("Windows Credential Manager could not save the credential.")));
            }
            finally
            {
                Marshal.FreeCoTaskMem(credential.TargetName);
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Unavailable, "Windows Credential Manager is unavailable; the password was not saved."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (userPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(userPointer);
            if (blobPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPointer);
        }
    }

    public ValueTask<CredentialOperationResult> DeleteAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var deleted = CredDelete(Target(profileId, kind), CredentialTypeGeneric, 0);
            var status = deleted || Marshal.GetLastWin32Error() == ErrorNotFound
                ? CredentialStoreStatus.Deleted
                : StatusFromLastError();
            return ValueTask.FromResult(new CredentialOperationResult(status, status == CredentialStoreStatus.Deleted ? null : DiagnosticFromLastError("Windows Credential Manager could not delete the credential.")));
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return ValueTask.FromResult(new CredentialOperationResult(CredentialStoreStatus.Unavailable, "Windows Credential Manager is unavailable; the credential was not deleted."));
        }
    }

    public async ValueTask<CredentialOperationResult> ClearProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var statuses = new[]
        {
            await DeleteAsync(profileId, ProfileCredentialKind.Sasl, cancellationToken).ConfigureAwait(false),
            await DeleteAsync(profileId, ProfileCredentialKind.ServerPassword, cancellationToken).ConfigureAwait(false),
            await DeleteAsync(profileId, ProfileCredentialKind.NickServ, cancellationToken).ConfigureAwait(false)
        };
        var failure = statuses.FirstOrDefault(result => result.Status is not (CredentialStoreStatus.Deleted or CredentialStoreStatus.Missing));
        return failure ?? new CredentialOperationResult(CredentialStoreStatus.Deleted);
    }

    private static string Target(Guid profileId, ProfileCredentialKind kind) => $"nexIRC5/profile/{profileId:D}/{kind}";

    private static CredentialStoreStatus StatusFromLastError() => Marshal.GetLastWin32Error() switch
    {
        ErrorNotFound => CredentialStoreStatus.Missing,
        ErrorAccessDenied => CredentialStoreStatus.AccessDenied,
        _ => CredentialStoreStatus.Failed
    };

    private static string DiagnosticFromLastError(string prefix) => $"{prefix} Windows error {Marshal.GetLastWin32Error()}; no plaintext fallback was used.";

    private static bool IsStorageException(Exception exception) => exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException or UnauthorizedAccessException or IOException;

    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserNamePointer;

        public string? UserNameText => UserNamePointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(UserNamePointer);
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string targetName, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string targetName, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);
}
