using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using nexIRC.Core.Session;

namespace nexIRC.Application;

/// <summary>
/// Windows DPAPI CurrentUser protection for native-resume bearer tokens. The
/// user profile owns the protection key; nexIRC owns no encryption key file.
/// The network binding is supplied as additional DPAPI entropy so a protected
/// blob copied to another profile/network cannot be silently rebound.
/// </summary>
public sealed class WindowsDpapiResumeSecretProtector : IResumeSecretProtector
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public byte[] Protect(string secret, string protectionContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var secretBytes = StrictUtf8.GetBytes(secret);
        try
        {
            return ProtectBytes(secretBytes, protectionContext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public string Unprotect(ReadOnlySpan<byte> protectedSecret, string protectionContext)
    {
        if (protectedSecret.Length == 0)
        {
            throw new CryptographicException("The protected resume secret was empty.");
        }

        var clear = UnprotectBytes(protectedSecret, protectionContext);
        try
        {
            return StrictUtf8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static byte[] ProtectBytes(ReadOnlySpan<byte> clear, string protectionContext)
    {
        EnsureWindows();
        var contextHash = ContextHash(protectionContext);
        var clearPointer = IntPtr.Zero;
        var entropyPointer = IntPtr.Zero;
        var entropyBlobPointer = IntPtr.Zero;
        try
        {
            clearPointer = Allocate(clear);
            entropyPointer = Allocate(contextHash);
            var entropy = new DataBlob(contextHash.Length, entropyPointer);
            entropyBlobPointer = Marshal.AllocHGlobal(Marshal.SizeOf<DataBlob>());
            Marshal.StructureToPtr(entropy, entropyBlobPointer, fDeleteOld: false);
            var input = new DataBlob(clear.Length, clearPointer);
            if (!CryptProtectData(
                ref input,
                "nexIRC native resume",
                entropyBlobPointer,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
            {
                throw new CryptographicException($"Windows could not protect native-resume state (error {Marshal.GetLastWin32Error()}).");
            }

            try
            {
                if (output.pbData == IntPtr.Zero || output.cbData <= 0 || output.cbData > 16 * 1024)
                {
                    throw new CryptographicException("Windows returned an invalid protected native-resume payload.");
                }

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                _ = LocalFree(output.pbData);
            }
        }
        finally
        {
            if (clearPointer != IntPtr.Zero)
            {
                ZeroAndFree(clearPointer, clear.Length);
            }

            if (entropyPointer != IntPtr.Zero)
            {
                ZeroAndFree(entropyPointer, contextHash.Length);
            }

            if (entropyBlobPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyBlobPointer);
            }

            CryptographicOperations.ZeroMemory(contextHash);
        }
    }

    private static byte[] UnprotectBytes(ReadOnlySpan<byte> protectedBytes, string protectionContext)
    {
        EnsureWindows();
        var contextHash = ContextHash(protectionContext);
        var protectedPointer = IntPtr.Zero;
        var entropyPointer = IntPtr.Zero;
        var entropyBlobPointer = IntPtr.Zero;
        try
        {
            protectedPointer = Allocate(protectedBytes);
            entropyPointer = Allocate(contextHash);
            var entropy = new DataBlob(contextHash.Length, entropyPointer);
            entropyBlobPointer = Marshal.AllocHGlobal(Marshal.SizeOf<DataBlob>());
            Marshal.StructureToPtr(entropy, entropyBlobPointer, fDeleteOld: false);
            var input = new DataBlob(protectedBytes.Length, protectedPointer);
            if (!CryptUnprotectData(
                ref input,
                IntPtr.Zero,
                entropyBlobPointer,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
            {
                throw new CryptographicException($"Windows could not unprotect native-resume state (error {Marshal.GetLastWin32Error()}).");
            }

            try
            {
                if (output.pbData == IntPtr.Zero || output.cbData <= 0 || output.cbData > 4096)
                {
                    throw new CryptographicException("Windows returned an invalid native-resume secret.");
                }

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                _ = LocalFree(output.pbData);
            }
        }
        finally
        {
            if (protectedPointer != IntPtr.Zero)
            {
                ZeroAndFree(protectedPointer, protectedBytes.Length);
            }

            if (entropyPointer != IntPtr.Zero)
            {
                ZeroAndFree(entropyPointer, contextHash.Length);
            }

            if (entropyBlobPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyBlobPointer);
            }

            CryptographicOperations.ZeroMemory(contextHash);
        }
    }

    private static byte[] ContextHash(string protectionContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectionContext);
        return SHA256.HashData(StrictUtf8.GetBytes(protectionContext));
    }

    private static IntPtr Allocate(ReadOnlySpan<byte> bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes.ToArray(), 0, pointer, bytes.Length);
        return pointer;
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (length > 0)
        {
            var zeroes = new byte[length];
            Marshal.Copy(zeroes, 0, pointer, length);
        }

        Marshal.FreeHGlobal(pointer);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is available only on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int cbData, IntPtr pbData)
    {
        public readonly int cbData = cbData;
        public readonly IntPtr pbData = pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

/// <summary>
/// One bounded JSON record per network/profile. The JSON contains metadata and
/// DPAPI ciphertext only; no token, password, SASL payload, or transcript data.
/// </summary>
public sealed class JsonResumeStateStore : IResumeStateStore, IDisposable
{
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false
    };
    private readonly string _root;
    private readonly Action<ResumeStatePersistenceObservation>? _persistenceObserver;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public JsonResumeStateStore(string root)
        : this(root, persistenceObserver: null)
    {
    }

    internal JsonResumeStateStore(
        string root,
        Action<ResumeStatePersistenceObservation>? persistenceObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _persistenceObserver = persistenceObserver;
        Directory.CreateDirectory(_root);
    }

    public string RootPath => _root;

    public async ValueTask<ResumeStateLoadResult> LoadAsync(string networkIdentity, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(networkIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primary = GetPath(networkIdentity);
            var backup = primary + ".bak";
            var candidates = new[] { primary, backup };
            ResumeStateLoadStatus? failure = null;
            foreach (var path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length is <= 0 or > MaximumFileBytes)
                    {
                        throw new FormatException("The protected resume metadata exceeded its bound.");
                    }

                    var state = JsonSerializer.Deserialize<ClientResumeStateRecord>(bytes, JsonOptions);
                    if (state is null || !string.Equals(state.NetworkIdentity, networkIdentity, StringComparison.Ordinal))
                    {
                        throw new FormatException("The protected resume metadata binding was invalid.");
                    }

                    if (state.Version == 1)
                    {
                        // Version 1 contains one protected attachment token and
                        // no attachment ID or session grant. Preserve it as the
                        // primary attachment while making the schema upgrade explicit.
                        if (state.AttachmentId is not null || state.ProtectedSessionCredential is not null)
                        {
                            throw new FormatException("Version 1 resume metadata contained version 2 attachment fields.");
                        }

                        state = state with { Version = ClientResumeStateRecord.CurrentVersion };
                    }
                    else if (state.Version == 2)
                    {
                        state = state with { Version = ClientResumeStateRecord.CurrentVersion };
                    }

                    if (!state.IsSupported)
                    {
                        failure = ResumeStateLoadStatus.Unsupported;
                        continue;
                    }

                    if (!state.IsWellFormed)
                    {
                        throw new FormatException("The protected resume metadata was malformed.");
                    }

                    return new ResumeStateLoadResult(ResumeStateLoadStatus.Loaded, state);
                }
                catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
                {
                    failure = ResumeStateLoadStatus.Corrupt;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failure = ResumeStateLoadStatus.Unavailable;
                }
            }

            if (failure == ResumeStateLoadStatus.Corrupt && File.Exists(primary))
            {
                var quarantine = primary + ".bad";
                try
                {
                    File.Move(primary, quarantine, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            return failure is { } status
                ? new ResumeStateLoadResult(status, Detail: "Protected resume metadata was not usable; ordinary IRC fallback remains available.")
                : ResumeStateLoadResult.Missing;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ResumeStateStoreResult> SaveAsync(ClientResumeStateRecord state, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsSupported || !state.IsWellFormed)
        {
            return new ResumeStateStoreResult(ResumeStateStoreStatus.Corrupt, "The protected resume metadata was invalid.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = GetPath(state.NetworkIdentity);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            Observe(ResumeStatePersistenceStage.BeforeSerialization, state);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            if (bytes.Length > MaximumFileBytes)
            {
                return new ResumeStateStoreResult(ResumeStateStoreStatus.Corrupt, "The protected resume metadata exceeded its bound.");
            }

            Observe(ResumeStatePersistenceStage.AfterSerialization, state);
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                Observe(ResumeStatePersistenceStage.AfterTemporaryFileCreated, state);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                Observe(ResumeStatePersistenceStage.AfterWrite, state);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                Observe(ResumeStatePersistenceStage.AfterWriteThroughFlush, state);
            }

            Observe(ResumeStatePersistenceStage.BeforeAtomicReplacement, state);
            if (File.Exists(path))
            {
                File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path, overwrite: false);
            }

            Observe(ResumeStatePersistenceStage.AfterAtomicReplacement, state);
            return new ResumeStateStoreResult(ResumeStateStoreStatus.Stored);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return new ResumeStateStoreResult(ResumeStateStoreStatus.Unavailable, "The protected resume metadata location was inaccessible.");
        }
        catch (IOException)
        {
            return new ResumeStateStoreResult(ResumeStateStoreStatus.Failed, "The protected resume metadata could not be committed atomically.");
        }
        finally
        {
            try
            {
                Observe(ResumeStatePersistenceStage.BeforeTemporaryCleanup, state);
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }

                Observe(ResumeStatePersistenceStage.AfterTemporaryCleanup, state);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            _gate.Release();
        }
    }

    private void Observe(ResumeStatePersistenceStage stage, ClientResumeStateRecord state) =>
        _persistenceObserver?.Invoke(new ResumeStatePersistenceObservation(
            stage,
            state.TokenGeneration,
            state.PendingTokenGeneration,
            state.AcknowledgedTokenGeneration,
            state.AuthoritativeBoundary,
            state.ProtectedPairingRecoveryCredential is { Length: > 0 }));

    public async ValueTask<ResumeStateStoreResult> DeleteAsync(string networkIdentity, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(networkIdentity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existed = false;
            foreach (var path in new[] { GetPath(networkIdentity), GetPath(networkIdentity) + ".bak", GetPath(networkIdentity) + ".tmp", GetPath(networkIdentity) + ".bad" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    existed = true;
                }
            }

            return new ResumeStateStoreResult(existed ? ResumeStateStoreStatus.Deleted : ResumeStateStoreStatus.Missing);
        }
        catch (UnauthorizedAccessException)
        {
            return new ResumeStateStoreResult(ResumeStateStoreStatus.Unavailable, "The protected resume metadata could not be removed.");
        }
        catch (IOException)
        {
            return new ResumeStateStoreResult(ResumeStateStoreStatus.Failed, "The protected resume metadata could not be removed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Dispose();
        }
    }

    private string GetPath(string networkIdentity)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(networkIdentity))).ToLowerInvariant();
        return Path.Combine(_root, digest + ".json");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal enum ResumeStatePersistenceStage
{
    BeforeSerialization,
    AfterSerialization,
    AfterTemporaryFileCreated,
    AfterWrite,
    AfterWriteThroughFlush,
    BeforeAtomicReplacement,
    AfterAtomicReplacement,
    BeforeTemporaryCleanup,
    AfterTemporaryCleanup
}

internal readonly record struct ResumeStatePersistenceObservation(
    ResumeStatePersistenceStage Stage,
    int CurrentGeneration,
    int? PendingGeneration,
    int AcknowledgedGeneration,
    string AuthoritativeBoundary,
    bool PairingRecoveryPending);

public static class ResumeStatePaths
{
    public static string GetDefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "nexIRC",
        "resume");
}
