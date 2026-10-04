using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;

namespace nexIRC.Application;

public sealed record LocalConversationDraft(
    Guid ProfileId,
    string ConversationKey,
    string Text,
    long BaseRevision,
    string ServerText,
    long ServerRevision,
    bool HasConflict,
    string? PendingMutationId = null,
    long? PendingBaseRevision = null,
    string? PendingText = null,
    bool HasLocalChanges = false,
    bool ClearAfterSuccessfulSend = false);

public interface IConversationDraftStore
{
    IReadOnlyList<LocalConversationDraft> Load();

    bool Save(IReadOnlyList<LocalConversationDraft> drafts);
}

public sealed class MemoryConversationDraftStore : IConversationDraftStore
{
    private IReadOnlyList<LocalConversationDraft> _drafts = Array.Empty<LocalConversationDraft>();

    public IReadOnlyList<LocalConversationDraft> Load() => _drafts.ToArray();

    public bool Save(IReadOnlyList<LocalConversationDraft> drafts)
    {
        _drafts = drafts.ToArray();
        return true;
    }
}

/// <summary>Atomic local draft snapshots protected with the same OS-backed protector as resume credentials.</summary>
public sealed class ProtectedJsonConversationDraftStore(string path, IResumeSecretProtector protector) : IConversationDraftStore
{
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumDraftRecords = 4096;
    private const string ProtectionContext = "nexirc-local-conversation-drafts-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.GetFullPath(string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A local draft path is required.", nameof(path))
        : path);
    private readonly IResumeSecretProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    private readonly object _gate = new();

    public IReadOnlyList<LocalConversationDraft> Load()
    {
        lock (_gate)
        {
            if (TryLoad(_path, out var primary)) return primary;
            if (TryLoad(_path + ".bak", out var backup)) return backup;
            return Array.Empty<LocalConversationDraft>();
        }
    }

    public bool Save(IReadOnlyList<LocalConversationDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count > MaximumDraftRecords || drafts.Any(static draft => !IsValid(draft))
            || drafts.Select(static draft => (draft.ProfileId, draft.ConversationKey)).Distinct().Count() != drafts.Count)
            return false;
        try
        {
            var json = JsonSerializer.Serialize(drafts, JsonOptions);
            var clearBytes = Encoding.UTF8.GetBytes(json);
            if (clearBytes.Length > MaximumFileBytes) return false;
            var protectedBytes = _protector.Protect(Convert.ToBase64String(clearBytes), ProtectionContext);
            if (protectedBytes.Length is 0 or > MaximumFileBytes) return false;
            var document = JsonSerializer.SerializeToUtf8Bytes(Convert.ToBase64String(protectedBytes), JsonOptions);
            if (document.Length > MaximumFileBytes) return false;

            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrWhiteSpace(directory)) return false;
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough | FileOptions.SequentialScan))
                {
                    stream.Write(document);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true);
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException
            or JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryLoad(string path, out IReadOnlyList<LocalConversationDraft> drafts)
    {
        drafts = Array.Empty<LocalConversationDraft>();
        if (!File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumFileBytes) return false;
            var protectedText = JsonSerializer.Deserialize<string>(File.ReadAllBytes(path), JsonOptions);
            if (string.IsNullOrEmpty(protectedText)) return false;
            var clearTextBase64 = _protector.Unprotect(Convert.FromBase64String(protectedText), ProtectionContext);
            var clearBytes = Convert.FromBase64String(clearTextBase64);
            if (clearBytes.Length > MaximumFileBytes) return false;
            var loaded = JsonSerializer.Deserialize<LocalConversationDraft[]>(clearBytes, JsonOptions);
            if (loaded is null || loaded.Length > MaximumDraftRecords || loaded.Any(static draft => !IsValid(draft))) return false;
            if (loaded.Select(static draft => (draft.ProfileId, draft.ConversationKey)).Distinct().Count() != loaded.Length)
                return false;
            drafts = loaded;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException
            or JsonException or ArgumentException or FormatException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValid(LocalConversationDraft? draft)
    {
        if (draft is null) return false;
        return draft.ProfileId != Guid.Empty
            && !string.IsNullOrWhiteSpace(draft.ConversationKey)
            && draft.ConversationKey.Length <= 512 && !draft.ConversationKey.Any(char.IsControl)
            && NexIrcDraftStateProtocol.TryEncodePayload(draft.Text, out _)
            && NexIrcDraftStateProtocol.TryEncodePayload(draft.ServerText, out _)
            && draft.BaseRevision >= 0 && draft.ServerRevision >= 0
            && (draft.PendingMutationId is null || NexIrcDraftStateProtocol.IsValidMutationId(draft.PendingMutationId))
            && (draft.PendingBaseRevision is null || draft.PendingBaseRevision.Value >= 0)
            && (draft.PendingBaseRevision is null || draft.PendingBaseRevision.Value < long.MaxValue)
            && (draft.PendingText is null || NexIrcDraftStateProtocol.TryEncodePayload(draft.PendingText, out _));
    }
}
