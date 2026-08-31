using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nexIRC.Application;

public static class ProfileExportSchema
{
    public const int CurrentVersion = 1;
}

public sealed record ProfileExportDocument
{
    public int SchemaVersion { get; init; } = ProfileExportSchema.CurrentVersion;

    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;

    public List<NetworkProfile> Profiles { get; init; } = [];

    public ApplicationPreferences? Preferences { get; init; }
}

public enum ProfileImportCollisionPolicy
{
    CreateNew,
    Skip,
    Overwrite
}

public sealed record ProfileImportResult(
    IReadOnlyList<NetworkProfile> ImportedProfiles,
    int SkippedProfiles,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Bounded, non-secret portability for profile definitions and safe global
/// preferences. Passwords, protected-vault target names, and live protocol
/// state have no representation in <see cref="ProfileExportDocument"/>.
/// </summary>
public sealed class ProfilePortabilityService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ConfigurationService _configuration;
    private readonly int _maximumFileBytes;

    public ProfilePortabilityService(ConfigurationService configuration, int maximumFileBytes = ConfigurationLimits.MaximumExportFileBytes)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (maximumFileBytes < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _maximumFileBytes = maximumFileBytes;
    }

    public async ValueTask ExportAsync(string path, IEnumerable<Guid>? selectedProfileIds = null, bool includePreferences = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var ids = selectedProfileIds?.ToHashSet() ?? _configuration.Profiles.Profiles.Select(profile => profile.Id).ToHashSet();
        var profiles = _configuration.Profiles.Profiles.Where(profile => ids.Contains(profile.Id)).ToList();
        var document = new ProfileExportDocument
        {
            SchemaVersion = ProfileExportSchema.CurrentVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Profiles = profiles,
            Preferences = includePreferences ? SafePreferences(_configuration.Preferences) : null
        };
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The export path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.Length > _maximumFileBytes)
            {
                throw new IOException("The profile export exceeds the supported size limit.");
            }

            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public async ValueTask<ProfileImportResult> ImportAsync(
        string path,
        ProfileImportCollisionPolicy collisionPolicy = ProfileImportCollisionPolicy.CreateNew,
        bool importPreferences = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ProfileImportResult([], 0, [$"The profile export could not be read safely: {exception.Message}"]);
        }
        ProfileExportDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ProfileExportDocument>(bytes, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new ProfileImportResult([], 0, [$"The profile export is malformed: {exception.Message}"]);
        }

        if (document is null)
        {
            return new ProfileImportResult([], 0, ["The profile export is empty."]);
        }

        if (document.SchemaVersion != ProfileExportSchema.CurrentVersion)
        {
            return new ProfileImportResult([], 0, [$"Profile export schema version {document.SchemaVersion} is not supported."]);
        }

        var imported = new List<NetworkProfile>();
        var diagnostics = new List<string>();
        var skipped = 0;
        foreach (var rawProfile in (document.Profiles ?? []).Take(ConfigurationLimits.MaximumProfiles))
        {
            var profile = ConfigurationValidator.NormalizeProfile(rawProfile);
            if (profile is null)
            {
                skipped++;
                diagnostics.Add("A profile with invalid host or nickname was skipped.");
                continue;
            }

            var existing = _configuration.Profiles.Profiles.FirstOrDefault(item => item.Id == profile.Id)
                ?? imported.FirstOrDefault(item => item.Id == profile.Id);
            if (existing is not null)
            {
                if (collisionPolicy == ProfileImportCollisionPolicy.Skip)
                {
                    skipped++;
                    continue;
                }

                if (collisionPolicy == ProfileImportCollisionPolicy.Overwrite)
                {
                    _configuration.Profiles.AddOrUpdate(profile);
                    imported.Add(profile);
                    continue;
                }

                profile = profile with { Id = CollisionId(profile.Id, imported.Count, _configuration.Profiles.Profiles.Select(item => item.Id).Concat(imported.Select(item => item.Id))) };
            }

            if (!_configuration.Profiles.AddOrUpdate(profile))
            {
                skipped++;
                diagnostics.Add("The profile limit was reached; remaining profiles were skipped.");
                break;
            }

            imported.Add(profile);
        }

        if (importPreferences && document.Preferences is { } preferences)
        {
            _configuration.SetPreferences(preferences with { LastSelectedNetworkProfileId = null });
        }

        return new ProfileImportResult(imported, skipped, diagnostics);
    }

    private async ValueTask<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The profile export was not found.", path);
        }

        if (info.Length > _maximumFileBytes)
        {
            throw new IOException("The profile export exceeds the supported size limit.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
        using var memory = new MemoryStream((int)info.Length);
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return memory.ToArray();
            }

            total += read;
            if (total > _maximumFileBytes)
            {
                throw new IOException("The profile export exceeds the supported size limit.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ApplicationPreferences SafePreferences(ApplicationPreferences preferences) => preferences with
    {
        LastSelectedNetworkProfileId = null
    };

    private static Guid CollisionId(Guid original, int index, IEnumerable<Guid> used)
    {
        var usedIds = used.ToHashSet();
        for (var attempt = 0; attempt < ConfigurationLimits.MaximumProfiles * 2; attempt++)
        {
            var material = Encoding.UTF8.GetBytes($"nexirc5-profile-import:{original:D}:{index}:{attempt}");
            var digest = SHA256.HashData(material);
            var bytes = digest[..16].ToArray();
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            var candidate = new Guid(bytes);
            if (candidate != Guid.Empty && usedIds.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not allocate a deterministic profile ID for import.");
    }
}
