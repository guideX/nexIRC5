using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum LogConversationKind { Channel, PrivateConversation, Status }

public enum LogMessageKind
{
    Message, Action, Notice, Ctcp, Join, Part, Quit, Kick, Nick, Topic, Mode, System, Error
}

public enum LogDirection { Incoming, Outgoing }

public enum ConversationLogSearchScope
{
    CurrentConversation,
    CurrentNetwork,
    AllHistory
}

public sealed record ConversationLogRecord
{
    /// <summary>Version of the durable JSONL record shape.</summary>
    public int SchemaVersion { get; init; } = ConversationHistorySchema.CurrentVersion;
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>
    /// Local delivery/processing time. Timestamp remains the displayed
    /// message time and may be authoritative server-time. This diagnostic is
    /// intentionally runtime-only so it does not expand every JSONL history
    /// record on the hot message path.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? ReceivedAt { get; init; }
    public Guid NetworkId { get; init; }
    public Guid ScopeId { get; init; }
    public Guid? ProfileId { get; init; }
    public LogConversationKind ConversationKind { get; init; }
    public string ConversationName { get; init; } = string.Empty;
    public string ConversationKey { get; init; } = string.Empty;
    public string? Sender { get; init; }
    public LogMessageKind MessageKind { get; init; }
    public LogDirection Direction { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool IsHighlight { get; init; }
    /// <summary>
    /// Server supplied identity only.  A missing value is not replaced by a
    /// hash of message content because repeated no-id messages are valid.
    /// </summary>
    public string? ServerMessageId { get; init; }
    /// <summary>
    /// Locally allocated ordering value, scoped to NetworkId and conversation.
    /// It is not a server identity and is never used to claim replay equality.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long DurableSequence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ConversationEntryProvenance Provenance { get; init; } = ConversationEntryProvenance.Live;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ConversationTimestampSource TimestampSource { get; init; } = ConversationTimestampSource.LegacyOrLocalReceiveTime;
    public string? BatchId { get; init; }
}

internal static class ConversationLogRecordValidation
{
    public static bool IsReadable(ConversationLogRecord? record) =>
        record is not null
        && record.Text is not null
        && record.ConversationName is not null
        && record.ConversationKey is not null
        && (record.ServerMessageId is null || record.ServerMessageId.Length <= 256)
        && (record.BatchId is null || record.BatchId.Length <= 256);
}

public sealed record ConversationLogQuery
{
    public ConversationLogSearchScope Scope { get; init; } = ConversationLogSearchScope.AllHistory;

    public Guid? HistoryScopeId { get; init; }

    public string? Text { get; init; }
    public Guid? NetworkId { get; init; }
    public Guid? ProfileId { get; init; }
    public LogConversationKind? ConversationKind { get; init; }
    public string? ConversationName { get; init; }

    /// <summary>
    /// Optional stable storage key for a mutable logical conversation. When
    /// present, it takes precedence over nickname equality for current-query
    /// navigation and search.
    /// </summary>
    public string? ConversationKey { get; init; }
    public string? Sender { get; init; }
    public LogMessageKind? MessageKind { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int MaximumResults { get; init; } = 100;
    public int Skip { get; init; }
}

public sealed record ConversationLogSearchLocation(
    Guid ScopeId,
    string ConversationKey,
    DateTimeOffset Timestamp,
    long? SourceOffset = null,
    int? SourceLength = null)
{
    /// <summary>
    /// Network identity is repeated here so a location is safe to hand across
    /// a cross-network result surface without consulting a row index or a
    /// transient view.
    /// </summary>
    public Guid NetworkId { get; init; }

    public LogConversationKind ConversationKind { get; init; }

    public string ConversationName { get; init; } = string.Empty;

    public long DurableSequence { get; init; }

    public string? ServerMessageId { get; init; }

    /// <summary>
    /// Source segment path for this session. It is an acceleration hint, never
    /// the source of truth.
    /// </summary>
    public string? SourcePath { get; init; }
}

/// <summary>
/// A bounded, navigation-ready search result. The legacy Record property is
/// retained for source compatibility; application and desktop code should use
/// the typed fields and Location instead of treating a result as raw JSON.
/// </summary>
public sealed record ConversationLogSearchResult
{
    public ConversationLogSearchResult(ConversationLogRecord record, string preview, ConversationLogSearchLocation? location = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        NetworkId = record.NetworkId;
        ScopeId = record.ScopeId;
        ProfileId = record.ProfileId;
        ConversationKind = record.ConversationKind;
        ConversationName = record.ConversationName;
        Timestamp = record.Timestamp;
        Sender = record.Sender;
        MessageKind = record.MessageKind;
        ArgumentNullException.ThrowIfNull(preview);
        Preview = preview.Length > ConfigurationLimits.MaximumSearchSnippetLength
            ? preview[..ConfigurationLimits.MaximumSearchSnippetLength]
            : preview;
        var effectiveConversationKey = string.IsNullOrWhiteSpace(record.ConversationKey)
            ? ConversationLoggingService.BuildConversationKey(record.ConversationKind, record.ConversationName)
            : record.ConversationKey;
        Location = (location ?? new ConversationLogSearchLocation(
            record.ScopeId,
            effectiveConversationKey,
            record.Timestamp)) with
        {
            NetworkId = record.NetworkId,
            ConversationKind = record.ConversationKind,
            ConversationName = record.ConversationName,
            DurableSequence = record.DurableSequence,
            ServerMessageId = record.ServerMessageId
        };
        CanonicalAnchor = Location.SourceOffset is long offset && Location.SourceLength is int length && length > 0
            ? new HistoryAnchorLocation(record, offset, length) { SourcePath = Location.SourcePath }
            : null;
    }

    public ConversationLogRecord Record { get; }

    /// <summary>
    /// The exact persisted record location when the result came from JSONL.
    /// No-msgid records remain distinct because the anchor includes their
    /// durable sequence and byte location.
    /// </summary>
    public HistoryAnchorLocation? CanonicalAnchor { get; }

    public Guid NetworkId { get; }

    public Guid ScopeId { get; }

    public Guid? ProfileId { get; }

    public LogConversationKind ConversationKind { get; }

    public string ConversationName { get; }

    public DateTimeOffset Timestamp { get; }

    public string? Sender { get; }

    public LogMessageKind MessageKind { get; }

    public string Preview { get; }

    public string Snippet => Preview;

    public string? ServerMessageId => Record.ServerMessageId;

    public long DurableSequence => Record.DurableSequence;

    public string DurableConversationKey => Location.ConversationKey;

    public LogMessageKind EventType => MessageKind;

    public ConversationLogSearchLocation Location { get; }
}

public sealed record ConversationLogSearchStatistics(
    long RecordsExamined,
    int FilesExamined,
    int FilesSkipped,
    long MatchingRecords,
    int ResultsProduced,
    bool ResultsTruncated,
    bool FilesTruncated,
    long RecordsSkippedByIndex = 0,
    int IndexFilesUsed = 0,
    long IndexBuildMilliseconds = 0,
    int IndexFilesBuilt = 0,
    string? ValidationError = null);

public sealed record ConversationLogSearchPage(
    IReadOnlyList<ConversationLogSearchResult> Results,
    ConversationLogSearchStatistics Statistics);

public sealed record ConversationLogCleanupStatistics(
    int SegmentsExamined,
    int SegmentsDeleted,
    int SegmentsRewritten,
    int SegmentsSkipped,
    long RecordsExamined,
    long RecordsRemoved,
    long BytesRead,
    long BytesWritten,
    bool FilesTruncated)
{
    public static ConversationLogCleanupStatistics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, false);
}

public sealed record HistoryPageRequest
{
    public required Guid ScopeId { get; init; }
    /// <summary>
    /// Optional for compatibility with profile-wide history tools. When set,
    /// local conversation paging is isolated to this logical network.
    /// </summary>
    public Guid? NetworkId { get; init; }
    public required LogConversationKind ConversationKind { get; init; }
    public required string ConversationName { get; init; }
    public string? ConversationKey { get; init; }
    public int PageSize { get; init; } = ConfigurationLimits.MaximumHistoryPageSize;
    public DateTimeOffset? Before { get; init; }
    public long? BeforeDurableSequence { get; init; }
    public DateTimeOffset? After { get; init; }
    public long? AfterDurableSequence { get; init; }
    public bool Oldest { get; init; }
    public DateTimeOffset? Around { get; init; }
    public TimeSpan AroundWindow { get; init; } = TimeSpan.FromHours(12);
}

public sealed record HistoryPage(
    IReadOnlyList<ConversationLogRecord> Records,
    bool HasOlder,
    bool HasNewer,
    DateTimeOffset? OldestTimestamp = null,
    DateTimeOffset? NewestTimestamp = null)
{
    public static HistoryPage Empty { get; } = new(Array.Empty<ConversationLogRecord>(), false, false);
}

public sealed record ConversationHistoryRange(
    IReadOnlyList<ConversationLogRecord> Records,
    bool IsTruncated);

public enum HistoryExportFormat
{
    PlainText,
    Jsonl
}

public sealed record HistoryExportRequest
{
    public required Guid ScopeId { get; init; }
    public Guid? NetworkId { get; init; }
    public required LogConversationKind ConversationKind { get; init; }
    public required string ConversationName { get; init; }
    public string? ConversationKey { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int MaximumRecords { get; init; } = ConfigurationLimits.MaximumHistoryExportRecords;
}

public interface IConversationLogStore : IAsyncDisposable
{
    ValueTask<bool> AppendAsync(ConversationLogRecord record, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ConversationLogRecord>> ReadPageAsync(Guid scopeId, LogConversationKind kind, string conversationName, int pageSize = ConfigurationLimits.MaximumHistoryPageSize, DateTimeOffset? before = null, CancellationToken cancellationToken = default);
    ValueTask<HistoryPage> ReadPageWindowAsync(HistoryPageRequest request, CancellationToken cancellationToken = default);
    ValueTask<ConversationHistoryRange> ReadRangeAsync(HistoryExportRequest request, CancellationToken cancellationToken = default);
    ValueTask<HistoryAnchorResult> FindByServerMessageIdAsync(HistoryServerMessageAnchorRequest request, CancellationToken cancellationToken = default);
    ValueTask<HistoryAnchorResult> FindByTimestampAsync(HistoryTimestampAnchorRequest request, CancellationToken cancellationToken = default);
    ValueTask<HistoryContextResult> ReadContextAroundAsync(HistoryContextRequest request, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ConversationLogSearchResult>> SearchAsync(ConversationLogQuery query, CancellationToken cancellationToken = default);
    ValueTask<ConversationLogSearchPage> SearchDetailedAsync(ConversationLogQuery query, CancellationToken cancellationToken = default);
    ValueTask<int> CleanupAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class ConversationLoggingService
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 12,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly IConversationLogStore _store;
    private readonly Func<ApplicationPreferences> _preferences;

    public ConversationLoggingService(IConversationLogStore store, Func<ApplicationPreferences> preferences)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    }

    public string? LastDiagnostic { get; private set; }

    public void Record(Guid networkId, Guid? profileId, WorkspaceView view, TranscriptEntry entry)
    {
        if (entry.Provenance == ConversationEntryProvenance.LocalHistory)
        {
            // Local reads are projection-only and must never feed the same
            // JSONL store back into itself. Server playback has its own
            // explicit candidate path and may be persisted once.
            return;
        }

        var preferences = _preferences();
        if (!preferences.ConversationLoggingEnabled
            || view.Kind == WorkspaceViewKind.Query && !preferences.PrivateMessageLoggingEnabled
            || view.Kind == WorkspaceViewKind.ServerStatus && !preferences.StatusLoggingEnabled)
        {
            return;
        }

        var kind = view.Kind switch
        {
            WorkspaceViewKind.Channel => LogConversationKind.Channel,
            WorkspaceViewKind.Query => LogConversationKind.PrivateConversation,
            _ => LogConversationKind.Status
        };
        var name = view is ChannelView channel ? channel.Channel : view is QueryView query ? query.Nickname : "status";
        var conversationKey = view is QueryView queryView
            ? queryView.HistoryConversationKey
            : BuildConversationKey(kind, name);
        var text = RedactConversationText(entry.Text);
        var record = new ConversationLogRecord
        {
            Timestamp = entry.Timestamp,
            ReceivedAt = entry.ReceivedAt,
            NetworkId = networkId,
            ScopeId = profileId ?? networkId,
            ProfileId = profileId,
            ConversationKind = kind,
            ConversationName = name,
            ConversationKey = conversationKey,
            Sender = entry.Sender,
            MessageKind = ToLogKind(entry.Kind),
            Direction = entry.IsOutgoing ? LogDirection.Outgoing : LogDirection.Incoming,
            Text = text,
            IsHighlight = entry.IsHighlight,
            ServerMessageId = entry.ServerMessageId,
            Provenance = entry.Provenance,
            TimestampSource = entry.TimestampSource,
            BatchId = entry.BatchId
        };
        _ = AppendSafeAsync(record);
    }

    public static string BuildConversationKey(LogConversationKind kind, string name) =>
        $"{kind}:{IrcCaseMappingComparer.Fold(name, IrcCaseMapping.Rfc1459)}";

    internal static string EffectiveConversationKey(ConversationLogRecord record) =>
        string.IsNullOrWhiteSpace(record.ConversationKey)
            ? BuildConversationKey(record.ConversationKind, record.ConversationName)
            : record.ConversationKey;

    public static async ValueTask<ConversationHistoryRange> ExportAsync(
        IConversationLogStore store,
        HistoryExportRequest request,
        string path,
        HistoryExportFormat format,
        int maximumFileBytes = ConfigurationLimits.MaximumExportFileBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        maximumFileBytes = Math.Max(1024, maximumFileBytes);
        var range = await store.ReadRangeAsync(request, cancellationToken).ConfigureAwait(false);
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The export path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        var written = 0;
        var truncated = range.IsTruncated;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (var record in ConversationHistoryOrdering.OrderAscending(range.Records))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var safeRecord = record with { Text = IrcSensitiveData.RedactLine(record.Text) };
                    var line = format == HistoryExportFormat.Jsonl
                        ? JsonSerializer.SerializeToUtf8Bytes(safeRecord, ExportJsonOptions)
                        : Encoding.UTF8.GetBytes($"[{record.Timestamp:O}] [{record.ConversationKind}:{record.ConversationName}] <{record.Sender ?? "system"}> {IrcSensitiveData.RedactLine(record.Text)}\n");
                    var newlineBytes = format == HistoryExportFormat.Jsonl ? 1 : 0;
                    if (written + line.Length + newlineBytes > maximumFileBytes)
                    {
                        truncated = true;
                        break;
                    }

                    await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                    if (format == HistoryExportFormat.Jsonl)
                    {
                        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                    }

                    written += line.Length + newlineBytes;
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            return range with { IsTruncated = truncated };
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A failed cleanup cannot invalidate the completed export.
            }
        }
    }

    private async Task AppendSafeAsync(ConversationLogRecord record)
    {
        try
        {
            if (!await _store.AppendAsync(record).ConfigureAwait(false))
            {
                LastDiagnostic = "Conversation logging queue is full; the message was not persisted.";
            }
        }
        catch (Exception exception)
        {
            LastDiagnostic = $"Conversation logging failed safely: {exception.Message}";
        }
    }

    private static string RedactConversationText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (IrcSensitiveData.IsSensitiveCommandLine(text))
        {
            return IrcSensitiveData.RedactLine(text);
        }

        return text.Length > ConfigurationLimits.MaximumLogRecordBytes
            ? text[..ConfigurationLimits.MaximumLogRecordBytes]
            : text;
    }

    private static LogMessageKind ToLogKind(TranscriptEntryKind kind) => kind switch
    {
        TranscriptEntryKind.Action or TranscriptEntryKind.OutgoingAction => LogMessageKind.Action,
        TranscriptEntryKind.Notice or TranscriptEntryKind.OutgoingNotice => LogMessageKind.Notice,
        TranscriptEntryKind.Ctcp or TranscriptEntryKind.OutgoingCtcp => LogMessageKind.Ctcp,
        TranscriptEntryKind.Join => LogMessageKind.Join,
        TranscriptEntryKind.Part => LogMessageKind.Part,
        TranscriptEntryKind.Quit => LogMessageKind.Quit,
        TranscriptEntryKind.Kick => LogMessageKind.Kick,
        TranscriptEntryKind.Nick => LogMessageKind.Nick,
        TranscriptEntryKind.Topic => LogMessageKind.Topic,
        TranscriptEntryKind.Mode => LogMessageKind.Mode,
        TranscriptEntryKind.Error => LogMessageKind.Error,
        TranscriptEntryKind.System or TranscriptEntryKind.Connection or TranscriptEntryKind.Registration => LogMessageKind.System,
        _ => LogMessageKind.Message
    };
}

public sealed class JsonlConversationLogStore : IConversationLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 12,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _root;
    private readonly int _maximumRecordBytes;
    private readonly long _maximumSegmentBytes;
    private readonly Channel<PendingWrite> _queue = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(2048)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task _writer;
    private readonly object _diagnosticGate = new();
    private readonly object _historyIndexGate = new();
    private readonly Dictionary<string, JsonlHistoryIndexSnapshot> _historyIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _searchIndexGate = new();
    private readonly Dictionary<string, JsonlSearchIndexSnapshot> _searchIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _nextDurableSequences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _serverIdentityIndexes = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastDiagnostic;
    private ConversationLogCleanupStatistics _lastCleanupStatistics = ConversationLogCleanupStatistics.Empty;
    private int _disposed;

    public JsonlConversationLogStore(
        string root,
        int maximumRecordBytes = ConfigurationLimits.MaximumLogRecordBytes,
        long maximumSegmentBytes = ConfigurationLimits.DefaultHistorySegmentBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _maximumRecordBytes = Math.Max(1024, maximumRecordBytes);
        _maximumSegmentBytes = Math.Clamp(maximumSegmentBytes, 1024L, ConfigurationLimits.MaximumHistoryFileBytes);
        _writer = Task.Run(WriterAsync);
    }

    public string RootPath => _root;
    public long MaximumSegmentBytes => _maximumSegmentBytes;
    public string? LastDiagnostic { get { lock (_diagnosticGate) return _lastDiagnostic; } }
    public ConversationLogCleanupStatistics LastCleanupStatistics => Volatile.Read(ref _lastCleanupStatistics);

    /// <summary>
    /// Diagnostic counters for the disposable history worker and its
    /// disposable in-memory index snapshots. No records or objects are
    /// exposed, so these properties do not extend their lifetime.
    /// </summary>
    public int PendingWriteCount => _queue.Reader.CanCount ? _queue.Reader.Count : -1;

    public bool IsWriterCompleted => _writer.IsCompleted;

    public int HistoryIndexCount
    {
        get
        {
            lock (_historyIndexGate)
            {
                return _historyIndexes.Count;
            }
        }
    }

    public int SearchIndexCount
    {
        get
        {
            lock (_searchIndexGate)
            {
                return _searchIndexes.Count;
            }
        }
    }

    public ValueTask<bool> AppendAsync(ConversationLogRecord record, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > _maximumRecordBytes)
        {
            SetDiagnostic("A conversation log record exceeded the supported size and was dropped.");
            return ValueTask.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new PendingWrite(record, completion)))
        {
            completion.TrySetResult(false);
        }

        return new ValueTask<bool>(completion.Task);
    }

    public async ValueTask<IReadOnlyList<ConversationLogRecord>> ReadPageAsync(Guid scopeId, LogConversationKind kind, string conversationName, int pageSize = ConfigurationLimits.MaximumHistoryPageSize, DateTimeOffset? before = null, CancellationToken cancellationToken = default)
    {
        var page = await ReadPageWindowAsync(new HistoryPageRequest
        {
            ScopeId = scopeId,
            ConversationKind = kind,
            ConversationName = conversationName,
            PageSize = pageSize,
            Before = before
        }, cancellationToken).ConfigureAwait(false);
        return page.Records;
    }

    public async ValueTask<HistoryPage> ReadPageWindowAsync(HistoryPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var pageSize = Math.Clamp(request.PageSize, 1, ConfigurationLimits.MaximumHistoryPageSize);
        var conversationKey = request.ConversationKey ?? ConversationLoggingService.BuildConversationKey(request.ConversationKind, request.ConversationName);
        var paths = GetConversationPaths(request.ScopeId, conversationKey);
        if (paths.Length == 0)
        {
            return HistoryPage.Empty;
        }

        var selected = new List<ConversationLogRecord>(pageSize + 1);
        var preferOldest = (request.After is not null || request.Oldest) && request.Around is null;
        var hasOlder = false;
        var hasNewer = false;
        DateTimeOffset? discardedOldest = null;
        DateTimeOffset? discardedNewest = null;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                continue;
            }

            var segmentPage = await TryReadIndexedPageAsync(path, request, pageSize, cancellationToken).ConfigureAwait(false)
                ?? await ReadPageByScanAsync(path, request, pageSize, cancellationToken).ConfigureAwait(false);
            if (segmentPage is not null && segmentPage.Records.Count > 0)
            {
                hasOlder |= segmentPage.HasOlder;
                hasNewer |= segmentPage.HasNewer;
                selected.AddRange(segmentPage.Records);
                selected.Sort(request.Around is not null
                    ? (left, right) => CompareAround(left, right, request.Around.Value)
                    : preferOldest ? CompareAscending : CompareDescending);
                while (selected.Count > pageSize)
                {
                    var discarded = selected[^1];
                    selected.RemoveAt(selected.Count - 1);
                    discardedOldest = discardedOldest is null || discarded.Timestamp < discardedOldest
                        ? discarded.Timestamp
                        : discardedOldest;
                    discardedNewest = discardedNewest is null || discarded.Timestamp > discardedNewest
                        ? discarded.Timestamp
                        : discardedNewest;
                }
            }
        }

        if (selected.Count == 0)
        {
            return HistoryPage.Empty;
        }

        selected = ConversationHistoryMerge.DeduplicateExact(selected).ToList();
        var records = ConversationHistoryOrdering.OrderDescending(selected).ToArray();
        var pageOldest = records[^1].Timestamp;
        var pageNewest = records[0].Timestamp;
        hasOlder |= discardedOldest < pageOldest;
        hasNewer |= discardedNewest > pageNewest;
        return new HistoryPage(records, hasOlder, hasNewer, pageOldest, pageNewest);
    }

    public async ValueTask<ConversationHistoryRange> ReadRangeAsync(HistoryExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var maximum = Math.Clamp(request.MaximumRecords, 1, ConfigurationLimits.MaximumHistoryExportRecords);
        var conversationKey = request.ConversationKey ?? ConversationLoggingService.BuildConversationKey(request.ConversationKind, request.ConversationName);
        var paths = GetConversationPaths(request.ScopeId, conversationKey);
        if (paths.Length == 0)
        {
            return new ConversationHistoryRange(Array.Empty<ConversationLogRecord>(), false);
        }

        var records = new List<ConversationLogRecord>(Math.Min(maximum, ConfigurationLimits.MaximumHistoryPageSize));
        var truncated = false;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                continue;
            }

            var segmentRecords = await ReadRangeFromSegmentAsync(path, request, maximum - records.Count, cancellationToken).ConfigureAwait(false);
            if (segmentRecords is null)
            {
                continue;
            }

            if (segmentRecords.IsTruncated)
            {
                truncated = true;
            }

            records.AddRange(segmentRecords.Records);
            if (records.Count >= maximum)
            {
                records = records.Take(maximum).ToList();
                truncated = true;
                break;
            }
        }

        return new ConversationHistoryRange(ConversationHistoryOrdering.OrderAscending(
            ConversationHistoryMerge.DeduplicateExact(records)).ToArray(), truncated);
    }

    public async ValueTask<HistoryAnchorResult> FindByServerMessageIdAsync(
        HistoryServerMessageAnchorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messageId = ConversationEntryIdentity.NormalizeServerMessageId(request.ServerMessageId);
        if (messageId is null)
        {
            return HistoryAnchorResult.Missing;
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var locations = new List<HistoryAnchorLocation>();
        foreach (var path in GetConversationPaths(request.Conversation.ScopeId, request.Conversation.EffectiveConversationKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = await GetHistoryIndexAsync(
                path,
                request.Conversation.ScopeId,
                request.Conversation.ConversationKind,
                request.Conversation.ConversationName,
                request.Conversation.ConversationKey,
                cancellationToken,
                allowSmallFile: true).ConfigureAwait(false);
            if (index is null)
            {
                continue;
            }

            var entries = index.Entries
                .Where(entry => entry.NetworkId == request.Conversation.NetworkId
                    && string.Equals(entry.ServerMessageId, messageId, StringComparison.Ordinal))
                .ToArray();
            if (entries.Length == 0)
            {
                continue;
            }

            var loaded = await ReadIndexedLocationsAsync(path, entries, request.Conversation, cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                locations.AddRange(loaded);
            }
        }

        var anchor = locations
            .OrderBy(item => item, Comparer<HistoryAnchorLocation>.Create(HistoryAnchorPolicy.Compare))
            .FirstOrDefault();
        return anchor is null
            ? HistoryAnchorResult.Missing
            : new HistoryAnchorResult(HistoryAnchorMatch.Exact, anchor);
    }

    public async ValueTask<HistoryAnchorResult> FindByTimestampAsync(
        HistoryTimestampAnchorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await ReadIndexedAnchorCandidatesAsync(request.Conversation, cancellationToken).ConfigureAwait(false);
        if (candidates is null)
        {
            return HistoryAnchorResult.Missing;
        }

        var selected = HistoryAnchorPolicy.SelectTimestamp(
            candidates.Select(item => item.Location),
            request.Timestamp,
            request.Direction);
        return await MaterializeAnchorResultAsync(selected, candidates, request.Conversation, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<HistoryContextResult> ReadContextAroundAsync(
        HistoryContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var beforeCount = Math.Clamp(request.BeforeCount, 0, ConfigurationLimits.MaximumHistoryContextEntries);
        var afterCount = Math.Clamp(request.AfterCount, 0, ConfigurationLimits.MaximumHistoryContextEntries);
        HistoryAnchorResult anchor;
        if (request.CanonicalAnchor is { } canonicalAnchor)
        {
            anchor = await FindByCanonicalAnchorAsync(request.Conversation, canonicalAnchor, cancellationToken).ConfigureAwait(false);
        }
        else if (request.ServerMessageId is { Length: > 0 } messageId)
        {
            anchor = await FindByServerMessageIdAsync(new HistoryServerMessageAnchorRequest
            {
                Conversation = request.Conversation,
                ServerMessageId = messageId
            }, cancellationToken).ConfigureAwait(false);
        }
        else if (request.Timestamp is { } timestamp)
        {
            anchor = await FindByTimestampAsync(new HistoryTimestampAnchorRequest
            {
                Conversation = request.Conversation,
                Timestamp = timestamp,
                Direction = request.TimestampDirection
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return HistoryContextResult.Missing;
        }

        if (!anchor.Found || anchor.Anchor is null)
        {
            return new HistoryContextResult(anchor, Array.Empty<ConversationLogRecord>(), false);
        }

        var candidates = await ReadIndexedAnchorCandidatesAsync(request.Conversation, cancellationToken).ConfigureAwait(false);
        if (candidates is null || candidates.Count > ConfigurationLimits.MaximumHistoryIndexEntries)
        {
            // The exact anchor remains useful to a caller, but a context
            // window is deliberately not built from an unbounded aggregate.
            return new HistoryContextResult(anchor, Array.Empty<ConversationLogRecord>(), false);
        }

        var ordered = candidates
            .OrderBy(item => item.Location, Comparer<HistoryAnchorLocation>.Create(HistoryAnchorPolicy.Compare))
            .ToArray();
        var anchorIndex = Array.FindIndex(ordered, item =>
            string.Equals(item.Location.SourcePath, anchor.Anchor.SourcePath, StringComparison.OrdinalIgnoreCase)
            && item.Location.SourceOffset == anchor.Anchor.SourceOffset);
        if (anchorIndex < 0)
        {
            return new HistoryContextResult(anchor, Array.Empty<ConversationLogRecord>(), false);
        }

        var first = Math.Max(0, anchorIndex - beforeCount);
        var last = Math.Min(ordered.Length - 1, anchorIndex + afterCount);
        var selected = ordered[first..(last + 1)];
        var records = new List<ConversationLogRecord>(selected.Length);
        foreach (var group in selected.GroupBy(item => item.Location.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = group.Select(item => item.Entry).ToArray();
            var loaded = await ReadIndexedLocationsAsync(group.Key!, entries, request.Conversation, cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                records.AddRange(loaded.Select(item => item.Record));
            }
        }

        var canonical = ConversationHistoryOrdering.OrderAscending(
            ConversationHistoryMerge.DeduplicateExact(records)).ToArray();
        return new HistoryContextResult(anchor, canonical, canonical.Length == selected.Length);
    }

    private async Task<HistoryAnchorResult> FindByCanonicalAnchorAsync(
        HistoryConversationAddress address,
        HistoryAnchorLocation canonical,
        CancellationToken cancellationToken)
    {
        if (canonical.SourcePath is null
            || canonical.SourceOffset < 0
            || canonical.SourceLength <= 0)
        {
            return HistoryAnchorResult.Missing;
        }

        var path = GetConversationPaths(address.ScopeId, address.EffectiveConversationKey)
            .FirstOrDefault(candidate => string.Equals(candidate, canonical.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (path is null)
        {
            return HistoryAnchorResult.Missing;
        }

        var index = await GetHistoryIndexAsync(
            path,
            address.ScopeId,
            address.ConversationKind,
            address.ConversationName,
            address.ConversationKey,
            cancellationToken,
            allowSmallFile: true).ConfigureAwait(false);
        var entry = index?.Entries.FirstOrDefault(item =>
            item.Offset == canonical.SourceOffset
            && item.Length == canonical.SourceLength
            && item.NetworkId == address.NetworkId);
        if (entry is null)
        {
            return HistoryAnchorResult.Missing;
        }

        var loaded = await ReadIndexedLocationsAsync(path, [entry], address, cancellationToken).ConfigureAwait(false);
        var location = loaded?.SingleOrDefault();
        if (location is null
            || location.Record.Timestamp != canonical.Record.Timestamp
            || location.Record.DurableSequence != canonical.Record.DurableSequence
            || !string.Equals(location.Record.Text, canonical.Record.Text, StringComparison.Ordinal)
            || !string.Equals(
                ConversationEntryIdentity.NormalizeServerMessageId(location.Record.ServerMessageId),
                ConversationEntryIdentity.NormalizeServerMessageId(canonical.Record.ServerMessageId),
                StringComparison.Ordinal))
        {
            return HistoryAnchorResult.Missing;
        }

        return new HistoryAnchorResult(HistoryAnchorMatch.Exact, location);
    }

    private async Task<HistoryAnchorResult> MaterializeAnchorResultAsync(
        HistoryAnchorResult selected,
        IReadOnlyList<IndexedAnchorCandidate> candidates,
        HistoryConversationAddress address,
        CancellationToken cancellationToken)
    {
        if (!selected.Found)
        {
            return selected;
        }

        var selectedLocations = new[] { selected.Anchor, selected.Before, selected.After }
            .OfType<HistoryAnchorLocation>()
            .DistinctBy(item => (item.SourcePath, item.SourceOffset))
            .ToArray();
        var materialized = new List<HistoryAnchorLocation>();
        foreach (var group in selectedLocations.GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var entries = candidates
                .Where(item => string.Equals(item.Location.SourcePath, group.Key, StringComparison.OrdinalIgnoreCase)
                    && group.Any(selectedItem => selectedItem.SourceOffset == item.Location.SourceOffset))
                .Select(item => item.Entry)
                .ToArray();
            var loaded = await ReadIndexedLocationsAsync(group.Key!, entries, address, cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                materialized.AddRange(loaded);
            }
        }

        HistoryAnchorLocation? Resolve(HistoryAnchorLocation? location) => location is null
            ? null
            : materialized.FirstOrDefault(item =>
                string.Equals(item.SourcePath, location.SourcePath, StringComparison.OrdinalIgnoreCase)
                && item.SourceOffset == location.SourceOffset);

        var resolvedAnchor = Resolve(selected.Anchor);
        return resolvedAnchor is null
            ? HistoryAnchorResult.Missing
            : selected with { Anchor = resolvedAnchor, Before = Resolve(selected.Before), After = Resolve(selected.After) };
    }

    private async Task<List<IndexedAnchorCandidate>?> ReadIndexedAnchorCandidatesAsync(
        HistoryConversationAddress address,
        CancellationToken cancellationToken)
    {
        var candidates = new List<IndexedAnchorCandidate>();
        foreach (var path in GetConversationPaths(address.ScopeId, address.EffectiveConversationKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = await GetHistoryIndexAsync(
                path,
                address.ScopeId,
                address.ConversationKind,
                address.ConversationName,
                address.ConversationKey,
                cancellationToken,
                allowSmallFile: true).ConfigureAwait(false);
            if (index is null)
            {
                return null;
            }

            foreach (var entry in index.Entries.Where(item => item.NetworkId == address.NetworkId))
            {
                var synthetic = new ConversationLogRecord
                {
                    Timestamp = new DateTimeOffset(entry.TimestampTicks, TimeSpan.Zero),
                    NetworkId = entry.NetworkId,
                    ScopeId = address.ScopeId,
                    ConversationKind = address.ConversationKind,
                    ConversationName = address.ConversationName,
                    ConversationKey = address.EffectiveConversationKey,
                    ServerMessageId = entry.ServerMessageId,
                    DurableSequence = entry.DurableSequence
                };
                candidates.Add(new IndexedAnchorCandidate(
                    new HistoryAnchorLocation(synthetic, entry.Offset, entry.Length) { SourcePath = path },
                    entry));
            }
        }

        return candidates;
    }

    private async Task<HistoryPage?> ReadPageByScanAsync(
        string path,
        HistoryPageRequest request,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var selected = new List<ConversationLogRecord>(pageSize + 1);
        DateTimeOffset? oldest = null;
        DateTimeOffset? newest = null;
        var preferOldest = (request.After is not null || request.Oldest) && request.Around is null;
        try
        {
            await foreach (var record in ReadFileAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!IsConversationRecord(record, request.ScopeId, request.ConversationKind, request.ConversationName, request.ConversationKey, request.NetworkId))
                {
                    continue;
                }

                oldest = oldest is null || record.Timestamp < oldest ? record.Timestamp : oldest;
                newest = newest is null || record.Timestamp > newest ? record.Timestamp : newest;
                var eligible = request.Around is not null
                    ? record.Timestamp >= request.Around.Value - request.AroundWindow && record.Timestamp <= request.Around.Value + request.AroundWindow
                    : (request.Before is null || record.Timestamp < request.Before.Value)
                        && (request.After is null || record.Timestamp > request.After.Value);
                if (!eligible)
                {
                    continue;
                }

                selected.Add(record);
                selected.Sort(request.Around is not null
                    ? (left, right) => CompareAround(left, right, request.Around.Value)
                    : preferOldest ? CompareAscending : CompareDescending);
                if (selected.Count > pageSize) selected.RemoveAt(selected.Count - 1);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetDiagnostic($"Conversation history read failed safely: {exception.Message}");
        }

        var records = ConversationHistoryOrdering.OrderDescending(
            ConversationHistoryMerge.DeduplicateExact(selected)).ToArray();
        if (records.Length == 0)
        {
            return null;
        }

        var pageOldest = records[^1].Timestamp;
        var pageNewest = records[0].Timestamp;
        return new HistoryPage(records, oldest < pageOldest, newest > pageNewest, pageOldest, pageNewest);
    }

    private async Task<SegmentRangeRead?> ReadRangeFromSegmentAsync(
        string path,
        HistoryExportRequest request,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (maximum <= 0)
        {
            return new SegmentRangeRead(Array.Empty<ConversationLogRecord>(), false);
        }

        var index = await GetHistoryIndexAsync(path, request.ScopeId, request.ConversationKind, request.ConversationName, request.ConversationKey, cancellationToken).ConfigureAwait(false);
        if (index is not null)
        {
            var indexedEntries = index.Entries
                .Where(entry => request.From is null || entry.TimestampTicks >= request.From.Value.UtcTicks)
                .Where(entry => request.To is null || entry.TimestampTicks <= request.To.Value.UtcTicks)
                .ToArray();
            var indexedRecords = await ReadIndexedRecordsAsync(
                path,
                indexedEntries.Take(Math.Min(maximum, indexedEntries.Length)),
                request.ScopeId,
                request.ConversationKind,
                request.ConversationName,
                request.ConversationKey,
                request.NetworkId,
                cancellationToken).ConfigureAwait(false);
            if (indexedRecords is not null)
            {
                return new SegmentRangeRead(indexedRecords, indexedEntries.Length > maximum);
            }
        }

        var records = new List<ConversationLogRecord>(Math.Min(maximum, ConfigurationLimits.MaximumHistoryPageSize));
        var truncated = false;
        try
        {
            await foreach (var record in ReadFileAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!IsConversationRecord(record, request.ScopeId, request.ConversationKind, request.ConversationName, request.ConversationKey, request.NetworkId)
                    || request.From is not null && record.Timestamp < request.From.Value
                    || request.To is not null && record.Timestamp > request.To.Value)
                {
                    continue;
                }

                if (records.Count >= maximum)
                {
                    truncated = true;
                    continue;
                }

                records.Add(record);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetDiagnostic($"Conversation history export read failed safely: {exception.Message}");
        }

        return new SegmentRangeRead(records, truncated);
    }

    private static bool IsConversationRecord(
        ConversationLogRecord record,
        Guid scopeId,
        LogConversationKind conversationKind,
        string conversationName,
        string? conversationKey = null,
        Guid? networkId = null) =>
        record.ScopeId == scopeId
        && (networkId is null || record.NetworkId == networkId)
        && record.ConversationKind == conversationKind
        && (string.IsNullOrWhiteSpace(conversationKey)
            ? IrcIdentity.Equals(record.ConversationName, conversationName, IrcCaseMapping.Rfc1459)
            : string.Equals(EffectiveConversationKey(record), conversationKey, StringComparison.Ordinal));

    internal static string EffectiveConversationKey(ConversationLogRecord record) =>
        string.IsNullOrWhiteSpace(record.ConversationKey)
            ? ConversationLoggingService.BuildConversationKey(record.ConversationKind, record.ConversationName)
            : record.ConversationKey;

    public async ValueTask<IReadOnlyList<ConversationLogSearchResult>> SearchAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        var page = await SearchDetailedAsync(query, cancellationToken).ConfigureAwait(false);
        return page.Results;
    }

    public async ValueTask<ConversationLogSearchPage> SearchDetailedAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HistorySearchRequestValidator.TryNormalize(query, out var normalizedQuery, out var validationError))
        {
            return new ConversationLogSearchPage(
                Array.Empty<ConversationLogSearchResult>(),
                new ConversationLogSearchStatistics(0, 0, 0, 0, 0, false, false, ValidationError: validationError));
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
        query = normalizedQuery;
        var text = query.Text ?? string.Empty;
        var maximum = query.MaximumResults;
        var skip = query.Skip;
        var candidateLimit = Math.Min(ConfigurationLimits.MaximumSearchResults * 2, maximum + skip);
        var collector = new SearchResultCollector(candidateLimit);
        var recordsExamined = 0L;
        var filesExamined = 0;
        var filesSkipped = 0;
        var filesTruncated = false;
        var recordsSkippedByIndex = 0L;
        var indexFilesUsed = 0;
        var indexFilesBuilt = 0;
        var indexBuildMilliseconds = 0L;
        if (!Directory.Exists(_root))
        {
            return new ConversationLogSearchPage(
                Array.Empty<ConversationLogSearchResult>(),
                new ConversationLogSearchStatistics(0, 0, 0, 0, 0, false, false));
        }

        try
        {
            var paths = EnumerateSearchPaths(query, out filesTruncated);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesExamined++;
                FileInfo sourceInfo;
                try
                {
                    sourceInfo = new FileInfo(path);
                    if (sourceInfo.Length > ConfigurationLimits.MaximumHistoryFileBytes)
                    {
                        filesSkipped++;
                        continue;
                    }
                }
                catch (IOException)
                {
                    filesSkipped++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    filesSkipped++;
                    continue;
                }

                var scanSucceeded = true;
                JsonlSearchIndexBuilder? indexBuilder = null;
                Stopwatch? indexBuildStopwatch = null;
                try
                {
                    var indexLoad = await GetSearchIndexAsync(path, cancellationToken).ConfigureAwait(false);
                    var index = indexLoad?.Snapshot;
                    indexBuilder = index is null && sourceInfo.Length >= ConfigurationLimits.MinimumHistoryIndexFileBytes
                        ? new JsonlSearchIndexBuilder()
                        : null;
                    indexBuildStopwatch = indexBuilder is null ? null : Stopwatch.StartNew();
                    if (index is not null)
                    {
                        indexFilesUsed++;
                    }

                    JsonlSearchIndexBlock[] blocks = index is null
                        ? Array.Empty<JsonlSearchIndexBlock>()
                        : index.Blocks.Where(block => IsSearchBlockEligible(block, query, text)).ToArray();
                    var useIndexRanges = index is not null && blocks.Length < index.Blocks.Count;
                    if (useIndexRanges && index is not null)
                    {
                        recordsSkippedByIndex += index.Blocks.Sum(block => block.RecordCount) - blocks.Sum(block => block.RecordCount);
                    }

                    var ranges = !useIndexRanges
                        ? new (long Start, long? End)[] { (0L, null) }
                        : blocks.Select(block => (Start: block.Offset, End: (long?)(block.Offset + block.Length))).ToArray();
                    foreach (var range in ranges)
                    {
                        await foreach (var item in ReadFileWithPositionsAsync(path, cancellationToken, range.Start, range.End).ConfigureAwait(false))
                        {
                            recordsExamined++;
                            indexBuilder?.Add(item.Offset, item.Record);
                            if (!Matches(item.Record, query, text))
                            {
                                continue;
                            }

                            var record = item.Record;
                            collector.Add(new ConversationLogSearchResult(
                                record,
                                Preview(record.Text, text),
                                new ConversationLogSearchLocation(
                                    record.ScopeId,
                                    EffectiveConversationKey(record),
                                    record.Timestamp,
                                    item.Offset,
                                    item.Length)
                                {
                                    SourcePath = path
                                }));
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    filesSkipped++;
                    scanSucceeded = false;
                    SetDiagnostic($"Conversation log search skipped an unreadable file safely: {exception.Message}");
                }

                if (scanSucceeded && indexBuilder is not null)
                {
                    var builtBlocks = indexBuilder.Complete(sourceInfo.Length);
                    if (builtBlocks is not null)
                    {
                        var builtIndex = await JsonlSearchIndex.WriteBuiltAsync(
                            path,
                            sourceInfo.Length,
                            sourceInfo.LastWriteTimeUtc.Ticks,
                            builtBlocks,
                            cancellationToken).ConfigureAwait(false);
                        indexBuildStopwatch?.Stop();
                        if (builtIndex is not null)
                        {
                            indexFilesBuilt++;
                            indexBuildMilliseconds += indexBuildStopwatch?.ElapsedMilliseconds ?? 0;
                            if (JsonlSearchIndex.IsSidecarCurrent(JsonlSearchIndex.GetSidecarPath(path), builtIndex))
                            {
                                lock (_searchIndexGate)
                                {
                                    _searchIndexes[path] = builtIndex;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetDiagnostic($"Conversation log search failed safely: {exception.Message}");
        }

        var results = collector.Results
            .Skip(skip)
            .Take(maximum)
            .ToArray();
        return new ConversationLogSearchPage(
            results,
            new ConversationLogSearchStatistics(
                recordsExamined,
                filesExamined,
                filesSkipped,
                collector.MatchingRecords,
                results.Length,
                collector.MatchingRecords > (long)skip + maximum,
                filesTruncated,
                recordsSkippedByIndex,
                indexFilesUsed,
                indexBuildMilliseconds,
                indexFilesBuilt));
    }

    public async ValueTask<int> CleanupAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        var statistics = new CleanupStatisticsAccumulator();
        if (!Directory.Exists(_root))
        {
            Volatile.Write(ref _lastCleanupStatistics, statistics.ToRecord());
            return 0;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (statistics.SegmentsExamined >= ConfigurationLimits.MaximumHistorySourceFiles)
                {
                    statistics.FilesTruncated = true;
                    break;
                }

                statistics.SegmentsExamined++;
                RetentionScanResult scan;
                try
                {
                    var sourceInfo = new FileInfo(path);
                    if (!sourceInfo.Exists || sourceInfo.Length > ConfigurationLimits.MaximumHistoryFileBytes)
                    {
                        statistics.SegmentsSkipped++;
                        continue;
                    }

                    scan = await ScanForRetentionAsync(path, olderThan, sourceInfo.Length, cancellationToken).ConfigureAwait(false);
                    statistics.RecordsExamined += scan.RecordCount;
                    statistics.BytesRead += scan.BytesRead;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    statistics.SegmentsSkipped++;
                    SetDiagnostic($"Conversation history cleanup skipped a file safely: {exception.Message}");
                    continue;
                }

                if (!scan.IsSafe)
                {
                    statistics.SegmentsSkipped++;
                    SetDiagnostic("Conversation history cleanup skipped a file containing malformed records safely.");
                    continue;
                }

                if (scan.RecordCount == 0)
                {
                    if (scan.BytesRead == 0 && IsArchivedPath(path))
                    {
                        if (TryDeleteHistorySource(path))
                        {
                            statistics.SegmentsDeleted++;
                        }
                    }

                    continue;
                }

                if (scan.RemovedCount == 0)
                {
                    continue;
                }

                if (scan.RetainedCount == 0 && IsArchivedPath(path))
                {
                    if (TryDeleteHistorySource(path))
                    {
                        removed += scan.RemovedCount;
                        statistics.RecordsRemoved += scan.RemovedCount;
                        statistics.SegmentsDeleted++;
                    }

                    continue;
                }

                var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    var bytesWritten = await RewriteRetainedRecordsAsync(path, temporaryPath, olderThan, cancellationToken).ConfigureAwait(false);
                    File.Move(temporaryPath, path, true);
                    InvalidateHistoryIndex(path);
                    InvalidateSearchIndex(path);
                    removed += scan.RemovedCount;
                    statistics.RecordsRemoved += scan.RemovedCount;
                    statistics.BytesRead += scan.BytesRead;
                    statistics.BytesWritten += bytesWritten;
                    statistics.SegmentsRewritten++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    statistics.SegmentsSkipped++;
                    SetDiagnostic($"Conversation history cleanup skipped a file safely: {exception.Message}");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                    catch (IOException)
                    {
                        SetDiagnostic("Conversation history cleanup temporary-file removal was deferred safely.");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        SetDiagnostic("Conversation history cleanup temporary-file removal was deferred safely.");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetDiagnostic($"Conversation history cleanup enumeration failed safely: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _lastCleanupStatistics, statistics.ToRecord());
        }

        return removed;
    }

    private async Task<RetentionScanResult> ScanForRetentionAsync(
        string path,
        DateTimeOffset olderThan,
        long sourceLength,
        CancellationToken cancellationToken)
    {
        var recordCount = 0;
        var removedCount = 0;
        var retainedCount = 0;
        var safe = true;
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0 || line.Length > _maximumRecordBytes)
            {
                safe = false;
                continue;
            }

            ConversationLogRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ConversationLogRecord>(line, JsonOptions);
            }
            catch (JsonException)
            {
                safe = false;
                continue;
            }

            if (!ConversationLogRecordValidation.IsReadable(record)
                || record!.Text.Length > ConfigurationLimits.MaximumLogRecordBytes)
            {
                safe = false;
                continue;
            }

            recordCount++;
            if (record.Timestamp < olderThan)
            {
                removedCount++;
            }
            else
            {
                retainedCount++;
            }
        }

        return new RetentionScanResult(safe, recordCount, removedCount, retainedCount, sourceLength);
    }

    private async Task<long> RewriteRetainedRecordsAsync(
        string sourcePath,
        string temporaryPath,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken)
    {
        var bytesWritten = 0L;
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await foreach (var record in ReadFileAsync(sourcePath, cancellationToken).ConfigureAwait(false))
            {
                if (record.Timestamp < olderThan)
                {
                    continue;
                }

                var line = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                bytesWritten += line.Length + 1L;
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return bytesWritten;
    }

    private bool TryDeleteHistorySource(string path)
    {
        try
        {
            File.Delete(path);
            InvalidateHistoryIndex(path);
            InvalidateSearchIndex(path);
            return true;
        }
        catch (IOException exception)
        {
            SetDiagnostic($"Conversation history cleanup could not delete a segment safely: {exception.Message}");
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            SetDiagnostic($"Conversation history cleanup could not delete a segment safely: {exception.Message}");
            return false;
        }
    }

    private static bool IsArchivedPath(string path) => Path.GetFileNameWithoutExtension(path).Contains(".s", StringComparison.OrdinalIgnoreCase);

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        while (await _queue.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_queue.Writer.TryWrite(new PendingWrite(null, completion)))
            {
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        lock (_historyIndexGate)
        {
            _historyIndexes.Clear();
        }

        lock (_searchIndexGate)
        {
            _searchIndexes.Clear();
        }

        _nextDurableSequences.Clear();
        _serverIdentityIndexes.Clear();
    }

    private async Task WriterAsync()
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (pending.Record is null)
                {
                    pending.Completion.TrySetResult(true);
                    continue;
                }

                try
                {
                    var conversationKey = string.IsNullOrWhiteSpace(pending.Record.ConversationKey)
                        ? ConversationLoggingService.BuildConversationKey(pending.Record.ConversationKind, pending.Record.ConversationName)
                        : pending.Record.ConversationKey;
                    var path = GetPath(pending.Record.ScopeId, conversationKey);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var identity = ConversationEntryIdentity.GetServerIdentityKey(pending.Record);
                    var identities = await GetServerIdentityIndexAsync(path).ConfigureAwait(false);
                    if (identity is not null && identities.Contains(identity))
                    {
                        // Exact server-id replay is an accepted no-op. The
                        // caller must not mistake this for queue loss.
                        pending.Completion.TrySetResult(true);
                        continue;
                    }

                    var record = pending.Record with
                    {
                        SchemaVersion = ConversationHistorySchema.CurrentVersion,
                        DurableSequence = AllocateDurableSequence(pending.Record, path),
                        ServerMessageId = ConversationEntryIdentity.NormalizeServerMessageId(pending.Record.ServerMessageId)
                    };
                    var line = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                    var requiredBytes = (long)line.Length + 1;
                    if (requiredBytes > ConfigurationLimits.MaximumHistoryFileBytes)
                    {
                        SetDiagnostic("Conversation history file reached the supported 256 MiB bound; the new record was not persisted.");
                        pending.Completion.TrySetResult(false);
                        continue;
                    }

                    FileStream? stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var searchIndexBeforeAppend = GetCurrentSearchIndexForAppend(path, stream.Length);
                    var recordOffset = stream.Length;
                    var appendedSourceLength = checked((int)requiredBytes);
                    try
                    {
                        if (stream.Length > ConfigurationLimits.MaximumHistoryFileBytes
                            || requiredBytes > ConfigurationLimits.MaximumHistoryFileBytes - stream.Length)
                        {
                            SetDiagnostic("Conversation history file reached the supported 256 MiB bound; the new record was not persisted.");
                            pending.Completion.TrySetResult(false);
                            continue;
                        }

                        if (stream.Length > 0
                            && (stream.Length >= _maximumSegmentBytes
                                || requiredBytes > _maximumSegmentBytes - stream.Length))
                        {
                            await stream.DisposeAsync().ConfigureAwait(false);
                            stream = null;
                            RotateActiveSegment(path);
                            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            searchIndexBeforeAppend = null;
                            recordOffset = stream.Length;
                        }

                        recordOffset = stream.Position;
                        await stream!.WriteAsync(line).ConfigureAwait(false);
                        await stream.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        if (stream is not null)
                        {
                            await stream.DisposeAsync().ConfigureAwait(false);
                        }
                    }

                    lock (_historyIndexGate)
                    {
                        _historyIndexes.Remove(path);
                    }
                    JsonlSearchIndexSnapshot? updatedSearchIndex = null;
                    if (searchIndexBeforeAppend is not null)
                    {
                        try
                        {
                            updatedSearchIndex = await JsonlSearchIndex.TryAppendBuiltAsync(
                                path,
                                searchIndexBeforeAppend,
                                record,
                                recordOffset,
                                appendedSourceLength,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            // Search metadata is disposable. A failure after
                            // the canonical write must never report history as
                            // lost or turn a successful append into a retry.
                            SetDiagnostic($"Conversation search index append failed safely: {exception.Message}");
                        }
                    }
                    lock (_searchIndexGate)
                    {
                        if (updatedSearchIndex is not null
                            && JsonlSearchIndex.IsSidecarCurrent(JsonlSearchIndex.GetSidecarPath(path), updatedSearchIndex))
                        {
                            _searchIndexes[path] = updatedSearchIndex;
                        }
                        else
                        {
                            _searchIndexes.Remove(path);
                        }
                    }
                    if (identity is not null)
                    {
                        identities.Add(identity);
                    }
                    pending.Completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    SetDiagnostic($"Conversation log write failed safely: {exception.Message}");
                    pending.Completion.TrySetResult(false);
                }
            }
        }
        catch (Exception exception)
        {
            SetDiagnostic($"Conversation log writer stopped safely: {exception.Message}");
        }
    }

    private Task<HashSet<string>> GetServerIdentityIndexAsync(string activePath)
    {
        if (_serverIdentityIndexes.TryGetValue(activePath, out var existing))
        {
            return Task.FromResult(existing);
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var directory = Path.GetDirectoryName(activePath);
        var baseName = Path.GetFileNameWithoutExtension(activePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            try
            {
                foreach (var sourcePath in Directory.EnumerateFiles(directory, $"{baseName}*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    foreach (var line in File.ReadLines(sourcePath, Encoding.UTF8))
                    {
                        if (line.Length == 0 || line.Length > _maximumRecordBytes)
                        {
                            continue;
                        }

                        try
                        {
                            var record = JsonSerializer.Deserialize<ConversationLogRecord>(line, JsonOptions);
                            var identity = record is null ? null : ConversationEntryIdentity.GetServerIdentityKey(record);
                            if (identity is not null)
                            {
                                identities.Add(identity);
                            }
                        }
                        catch (JsonException)
                        {
                            // A malformed line is already bounded by the
                            // reader contract and cannot poison the index.
                        }
                    }
                }
            }
            catch (IOException exception)
            {
                SetDiagnostic($"Conversation identity index rebuild failed safely: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                SetDiagnostic($"Conversation identity index rebuild failed safely: {exception.Message}");
            }
        }

        _serverIdentityIndexes[activePath] = identities;
        return Task.FromResult(identities);
    }

    private long AllocateDurableSequence(ConversationLogRecord record, string activePath)
    {
        if (!_nextDurableSequences.TryGetValue(activePath, out var next))
        {
            next = 0;
            var directory = Path.GetDirectoryName(activePath);
            var baseName = Path.GetFileNameWithoutExtension(activePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                try
                {
                    foreach (var sourcePath in Directory.EnumerateFiles(directory, $"{baseName}*.jsonl", SearchOption.TopDirectoryOnly))
                    {
                        foreach (var line in File.ReadLines(sourcePath, Encoding.UTF8))
                        {
                            if (line.Length == 0 || line.Length > _maximumRecordBytes)
                            {
                                continue;
                            }

                            try
                            {
                                var existing = JsonSerializer.Deserialize<ConversationLogRecord>(line, JsonOptions);
                                if (existing is not null)
                                {
                                    next = Math.Max(next, existing.DurableSequence);
                                }
                            }
                            catch (JsonException)
                            {
                            }
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        var sequence = record.DurableSequence > 0
            ? record.DurableSequence
            : checked(next + 1);
        _nextDurableSequences[activePath] = Math.Max(next, sequence);
        return sequence;
    }

    private async IAsyncEnumerable<ConversationLogRecord> ReadFileAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > ConfigurationLimits.MaximumHistoryFileBytes) yield break;
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > _maximumRecordBytes) continue;
            ConversationLogRecord? record;
            try { record = JsonSerializer.Deserialize<ConversationLogRecord>(line, JsonOptions); } catch (JsonException) { continue; }
            if (ConversationLogRecordValidation.IsReadable(record) && record!.Text.Length <= ConfigurationLimits.MaximumLogRecordBytes) yield return record;
        }
    }

    private async IAsyncEnumerable<LocatedConversationLogRecord> ReadFileWithPositionsAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        long startOffset = 0,
        long? endOffset = null)
    {
        await foreach (var line in JsonlHistoryIndex.ReadLinesWithOffsetsAsync(path, _maximumRecordBytes, cancellationToken, startOffset, endOffset).ConfigureAwait(false))
        {
            ConversationLogRecord? record;
            try { record = JsonSerializer.Deserialize<ConversationLogRecord>(line.Bytes, JsonOptions); } catch (JsonException) { continue; }
            if (ConversationLogRecordValidation.IsReadable(record) && record!.Text.Length <= ConfigurationLimits.MaximumLogRecordBytes)
            {
                yield return new LocatedConversationLogRecord(record, line.Offset, line.Bytes.Length);
            }
        }
    }

    private string GetPath(Guid scopeId, string conversationKey)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(conversationKey)))[..24].ToLowerInvariant();
        return Path.Combine(_root, scopeId.ToString("N"), $"{hash}.jsonl");
    }

    private string[] GetConversationPaths(Guid scopeId, string conversationKey)
    {
        var activePath = GetPath(scopeId, conversationKey);
        var directory = Path.GetDirectoryName(activePath)!;
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var activeName = Path.GetFileName(activePath);
        var archivePrefix = Path.GetFileNameWithoutExtension(activePath) + ".s";
        var paths = new List<(int Sequence, string Path)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add((int.MaxValue, path));
                    continue;
                }

                if (!name.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var suffix = name[archivePrefix.Length..^".jsonl".Length];
                if (suffix.Length > 0 && int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence) && sequence >= 0)
                {
                    paths.Add((sequence, path));
                }
            }
        }
        catch (IOException exception)
        {
            SetDiagnostic($"Conversation history source discovery failed safely: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            SetDiagnostic($"Conversation history source discovery failed safely: {exception.Message}");
        }

        if (paths.All(item => !string.Equals(item.Path, activePath, StringComparison.OrdinalIgnoreCase)) && File.Exists(activePath))
        {
            paths.Add((int.MaxValue, activePath));
        }

        return paths
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path)
            .Take(ConfigurationLimits.MaximumHistorySourceFiles)
            .ToArray();
    }

    private void RotateActiveSegment(string activePath)
    {
        InvalidateHistoryIndex(activePath);
        InvalidateSearchIndex(activePath);
        var directory = Path.GetDirectoryName(activePath)!;
        var baseName = Path.GetFileNameWithoutExtension(activePath);
        var nextSequence = 0;
        foreach (var path in Directory.EnumerateFiles(directory, $"{baseName}.s*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var prefix = baseName + ".s";
            var suffix = name[prefix.Length..^".jsonl".Length];
            if (int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence))
            {
                nextSequence = Math.Max(nextSequence, sequence + 1);
            }
        }

        var archivePath = Path.Combine(directory, $"{baseName}.s{nextSequence:D8}.jsonl");
        while (File.Exists(archivePath))
        {
            nextSequence++;
            archivePath = Path.Combine(directory, $"{baseName}.s{nextSequence:D8}.jsonl");
        }

        File.Move(activePath, archivePath);
        using (File.Create(activePath))
        {
        }
    }

    private IEnumerable<string> EnumerateSearchPaths(ConversationLogQuery query, out bool filesTruncated)
    {
        filesTruncated = false;
        if (query.Scope == ConversationLogSearchScope.CurrentConversation
            && query.HistoryScopeId is Guid conversationScope
            && query.ConversationKind is LogConversationKind conversationKind
            && !string.IsNullOrWhiteSpace(query.ConversationName))
        {
            return GetConversationPaths(conversationScope, query.ConversationKey ?? ConversationLoggingService.BuildConversationKey(conversationKind, query.ConversationName));
        }

        var knownScope = query.HistoryScopeId ?? query.ProfileId;
        var searchRoot = knownScope is Guid scopeId
            ? Path.Combine(_root, scopeId.ToString("N"))
            : _root;
        if (!Directory.Exists(searchRoot))
        {
            return [];
        }

        var paths = Directory.EnumerateFiles(searchRoot, "*.jsonl", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(ConfigurationLimits.MaximumHistorySourceFiles + 1)
            .ToArray();
        filesTruncated = paths.Length > ConfigurationLimits.MaximumHistorySourceFiles;
        return paths.Take(ConfigurationLimits.MaximumHistorySourceFiles);
    }

    private async Task<SearchIndexLoadResult?> GetSearchIndexAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var sourceInfo = new FileInfo(path);
            if (!sourceInfo.Exists
                || sourceInfo.Length < ConfigurationLimits.MinimumHistoryIndexFileBytes
                || sourceInfo.Length > ConfigurationLimits.MaximumHistoryFileBytes)
            {
                return null;
            }

            lock (_searchIndexGate)
            {
                if (_searchIndexes.TryGetValue(path, out var cached)
                    && cached.SourceLength == sourceInfo.Length
                    && cached.SourceLastWriteTicks == sourceInfo.LastWriteTimeUtc.Ticks
                    && JsonlSearchIndex.IsSidecarCurrent(JsonlSearchIndex.GetSidecarPath(path), cached))
                {
                    if (JsonlSearchIndex.TryLoad(path, out var validated) && validated is not null)
                    {
                        _searchIndexes[path] = validated;
                        return new SearchIndexLoadResult(validated, 0);
                    }

                    _searchIndexes.Remove(path);
                }
            }

            if (!JsonlSearchIndex.TryLoad(path, out var loaded) || loaded is null)
            {
                return null;
            }

            if (JsonlSearchIndex.IsSidecarCurrent(JsonlSearchIndex.GetSidecarPath(path), loaded))
            {
                lock (_searchIndexGate)
                {
                    _searchIndexes[path] = loaded;
                }
            }

            return new SearchIndexLoadResult(loaded, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetDiagnostic($"Conversation search index was ignored safely: {exception.Message}");
            return null;
        }
    }

    private JsonlSearchIndexSnapshot? GetCurrentSearchIndexForAppend(string path, long sourceLength)
    {
        try
        {
            var sourceInfo = new FileInfo(path);
            if (!sourceInfo.Exists || sourceInfo.Length != sourceLength)
            {
                return null;
            }

            lock (_searchIndexGate)
            {
                if (_searchIndexes.TryGetValue(path, out var cached)
                    && cached.SourceLength == sourceLength
                    && cached.SourceLastWriteTicks == sourceInfo.LastWriteTimeUtc.Ticks
                    && JsonlSearchIndex.IsSidecarCurrent(JsonlSearchIndex.GetSidecarPath(path), cached))
                {
                    return cached;
                }
            }

            if (!JsonlSearchIndex.TryLoad(path, out var loaded) || loaded is null)
            {
                return null;
            }

            lock (_searchIndexGate)
            {
                _searchIndexes[path] = loaded;
            }

            return loaded;
        }
        catch (Exception exception)
        {
            SetDiagnostic($"Conversation search index append check was skipped safely: {exception.Message}");
            return null;
        }
    }

    private static bool IsSearchBlockEligible(JsonlSearchIndexBlock block, ConversationLogQuery query, string text) =>
        (query.From is null || block.MaximumTimestampTicks >= query.From.Value.UtcTicks)
        && (query.To is null || block.MinimumTimestampTicks <= query.To.Value.UtcTicks)
        && (!JsonlSearchIndex.CanUseTextFilter(text) || JsonlSearchIndex.MayContain(block, text));

    internal static bool Matches(ConversationLogRecord record, ConversationLogQuery query, string text)
    {
        var conversationMatches = string.IsNullOrWhiteSpace(query.ConversationKey)
            ? string.IsNullOrWhiteSpace(query.ConversationName) || IrcIdentity.Equals(record.ConversationName, query.ConversationName, IrcCaseMapping.Rfc1459)
            : string.Equals(EffectiveConversationKey(record), query.ConversationKey, StringComparison.Ordinal);
        if (query.Scope == ConversationLogSearchScope.CurrentConversation
            && (query.NetworkId is null || string.IsNullOrWhiteSpace(query.ConversationName)
                || record.NetworkId != query.NetworkId
                || query.ConversationKind is not null && record.ConversationKind != query.ConversationKind
                || !conversationMatches))
        {
            return false;
        }

        if (query.Scope == ConversationLogSearchScope.CurrentNetwork && (query.NetworkId is null || record.NetworkId != query.NetworkId))
        {
            return false;
        }

        return (string.IsNullOrEmpty(text)
                || record.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
                || record.Sender?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
                || record.ConversationName.Contains(text, StringComparison.OrdinalIgnoreCase))
            && (query.NetworkId is null || record.NetworkId == query.NetworkId)
            && (query.HistoryScopeId is null || record.ScopeId == query.HistoryScopeId)
            && (query.ProfileId is null || record.ProfileId == query.ProfileId || record.ScopeId == query.ProfileId)
            && (query.ConversationKind is null || record.ConversationKind == query.ConversationKind)
            && conversationMatches
            && (string.IsNullOrWhiteSpace(query.Sender) || record.Sender is not null && IrcCaseMappingComparer.Equals(record.Sender, query.Sender, IrcCaseMapping.Rfc1459))
            && (query.MessageKind is null || record.MessageKind == query.MessageKind)
            && (query.From is null || record.Timestamp >= query.From)
            && (query.To is null || record.Timestamp <= query.To);
    }

    private static string Preview(string text, string query)
    {
        const int maximum = ConfigurationLimits.MaximumSearchSnippetLength;
        var index = string.IsNullOrEmpty(query) ? 0 : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = index > 80 ? index - 80 : 0;
        var preview = text[start..Math.Min(text.Length, start + maximum)];
        return start > 0 ? "…" + preview : preview;
    }

    private static int CompareDescending(ConversationLogRecord left, ConversationLogRecord right)
        => ConversationHistoryOrdering.Compare(right, left);

    private static int CompareAscending(ConversationLogRecord left, ConversationLogRecord right)
        => ConversationHistoryOrdering.Compare(left, right);

    private static int CompareAround(ConversationLogRecord left, ConversationLogRecord right, DateTimeOffset around)
    {
        var leftDistance = Math.Abs((left.Timestamp - around).Ticks);
        var rightDistance = Math.Abs((right.Timestamp - around).Ticks);
        var distance = leftDistance.CompareTo(rightDistance);
        return distance != 0 ? distance : CompareDescending(left, right);
    }

    private void SetDiagnostic(string diagnostic) { lock (_diagnosticGate) _lastDiagnostic = diagnostic; }
    private sealed record PendingWrite(ConversationLogRecord? Record, TaskCompletionSource<bool> Completion);
    private sealed record LocatedConversationLogRecord(ConversationLogRecord Record, long Offset, int Length);
    private sealed record SearchIndexLoadResult(JsonlSearchIndexSnapshot Snapshot, long BuildMilliseconds);
    private sealed record SegmentRangeRead(IReadOnlyList<ConversationLogRecord> Records, bool IsTruncated);
    private sealed record RetentionScanResult(bool IsSafe, int RecordCount, int RemovedCount, int RetainedCount, long BytesRead);

    private sealed class CleanupStatisticsAccumulator
    {
        public int SegmentsExamined { get; set; }
        public int SegmentsDeleted { get; set; }
        public int SegmentsRewritten { get; set; }
        public int SegmentsSkipped { get; set; }
        public long RecordsExamined { get; set; }
        public long RecordsRemoved { get; set; }
        public long BytesRead { get; set; }
        public long BytesWritten { get; set; }
        public bool FilesTruncated { get; set; }

        public ConversationLogCleanupStatistics ToRecord() => new(
            SegmentsExamined,
            SegmentsDeleted,
            SegmentsRewritten,
            SegmentsSkipped,
            RecordsExamined,
            RecordsRemoved,
            BytesRead,
            BytesWritten,
            FilesTruncated);
    }

    private async Task<HistoryPage?> TryReadIndexedPageAsync(
        string path,
        HistoryPageRequest request,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var index = await GetHistoryIndexAsync(path, request.ScopeId, request.ConversationKind, request.ConversationName, request.ConversationKey, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            return null;
        }

        var scopedEntries = index.Entries.Where(entry => request.NetworkId is null || entry.NetworkId == request.NetworkId).ToArray();
        var eligible = scopedEntries.Where(entry => request.Around is not null
                ? entry.TimestampTicks >= request.Around.Value.UtcTicks - request.AroundWindow.Ticks
                    && entry.TimestampTicks <= request.Around.Value.UtcTicks + request.AroundWindow.Ticks
                : HistoryPageSelector.IsBefore(entry, request) && HistoryPageSelector.IsAfter(entry, request))
            .ToArray();
        var selected = request.Around is not null
            ? eligible.OrderBy(entry => Distance(entry.TimestampTicks, request.Around.Value.UtcTicks)).ThenByDescending(entry => entry.TimestampTicks).ThenByDescending(entry => entry.Offset).Take(pageSize).ToArray()
            : request.After is not null || request.Oldest
                ? eligible.OrderBy(entry => entry.TimestampTicks).ThenBy(entry => entry.Offset).Take(pageSize).ToArray()
                : eligible.OrderByDescending(entry => entry.TimestampTicks).ThenByDescending(entry => entry.Offset).Take(pageSize).ToArray();
        if (selected.Length == 0)
        {
            return HistoryPage.Empty;
        }

        var loaded = await ReadIndexedRecordsAsync(
            path,
            selected,
            request.ScopeId,
            request.ConversationKind,
            request.ConversationName,
            request.ConversationKey,
            request.NetworkId,
            cancellationToken).ConfigureAwait(false);
        if (loaded is null || loaded.Count != selected.Length)
        {
            return null;
        }

        var records = ConversationHistoryOrdering.OrderDescending(
            ConversationHistoryMerge.DeduplicateExact(loaded)).ToArray();
        var oldest = scopedEntries.Min(entry => entry.TimestampTicks);
        var newest = scopedEntries.Max(entry => entry.TimestampTicks);
        return new HistoryPage(
            records,
            oldest < records[^1].Timestamp.UtcTicks,
            newest > records[0].Timestamp.UtcTicks,
            records[^1].Timestamp,
            records[0].Timestamp);
    }

    private async Task<JsonlHistoryIndexSnapshot?> GetHistoryIndexAsync(
        string path,
        Guid scopeId,
        LogConversationKind conversationKind,
        string conversationName,
        string? conversationKey,
        CancellationToken cancellationToken,
        bool allowSmallFile = false)
    {
        try
        {
            var sourceInfo = new FileInfo(path);
            if (!sourceInfo.Exists
                || !allowSmallFile && sourceInfo.Length < ConfigurationLimits.MinimumHistoryIndexFileBytes
                || sourceInfo.Length > ConfigurationLimits.MaximumHistoryFileBytes)
            {
                return null;
            }

            lock (_historyIndexGate)
            {
                if (_historyIndexes.TryGetValue(path, out var cached)
                    && cached.SourceLength == sourceInfo.Length
                    && cached.SourceLastWriteTicks == sourceInfo.LastWriteTimeUtc.Ticks
                    && JsonlHistoryIndex.IsSidecarCurrent(JsonlHistoryIndex.GetSidecarPath(path), cached))
                {
                    return cached;
                }
            }

            var index = await JsonlHistoryIndex.LoadOrBuildAsync(
                path,
                scopeId,
                conversationKind,
                conversationName,
                conversationKey,
                _maximumRecordBytes,
                cancellationToken,
                allowSmallFile).ConfigureAwait(false);
            if (index is not null)
            {
                lock (_historyIndexGate)
                {
                    _historyIndexes[path] = index;
                }
            }

            return index;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetDiagnostic($"Conversation history index was ignored safely: {exception.Message}");
            return null;
        }
    }

    private static async Task<List<ConversationLogRecord>?> ReadIndexedRecordsAsync(
        string path,
        IEnumerable<JsonlHistoryIndexEntry> selected,
        Guid scopeId,
        LogConversationKind conversationKind,
        string conversationName,
        string? conversationKey,
        Guid? networkId,
        CancellationToken cancellationToken)
    {
        var entries = selected.ToArray();
        if (entries.Length == 0)
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            var records = new Dictionary<long, ConversationLogRecord>();
            foreach (var entry in entries.OrderBy(entry => entry.Offset))
            {
                var bytes = new byte[entry.Length];
                stream.Position = entry.Offset;
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize<ConversationLogRecord>(bytes, JsonOptions);
                if (!ConversationLogRecordValidation.IsReadable(record)
                    || record!.Text.Length > ConfigurationLimits.MaximumLogRecordBytes
                    || networkId is not null && record.NetworkId != networkId
                    || record.ScopeId != scopeId
                    || record.ConversationKind != conversationKind
                    || !IsConversationRecord(record, scopeId, conversationKind, conversationName, conversationKey)
                    || record.Timestamp.UtcTicks != entry.TimestampTicks)
                {
                    return null;
                }

                records[entry.Offset] = record;
            }

            return entries.Select(entry => records.TryGetValue(entry.Offset, out var record) ? record : null)
                .OfType<ConversationLogRecord>()
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<List<HistoryAnchorLocation>?> ReadIndexedLocationsAsync(
        string path,
        IEnumerable<JsonlHistoryIndexEntry> selected,
        HistoryConversationAddress address,
        CancellationToken cancellationToken)
    {
        var entries = selected.ToArray();
        if (entries.Length == 0)
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            var locations = new List<HistoryAnchorLocation>(entries.Length);
            foreach (var entry in entries.OrderBy(item => item.Offset))
            {
                var bytes = new byte[entry.Length];
                stream.Position = entry.Offset;
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize<ConversationLogRecord>(bytes, JsonOptions);
                if (!ConversationLogRecordValidation.IsReadable(record)
                    || record!.Text.Length > ConfigurationLimits.MaximumLogRecordBytes
                    || !HistoryAnchorPolicy.IsAddressMatch(record, address)
                    || record.Timestamp.UtcTicks != entry.TimestampTicks
                    || !string.Equals(
                        ConversationEntryIdentity.NormalizeServerMessageId(record.ServerMessageId),
                        entry.ServerMessageId,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                locations.Add(new HistoryAnchorLocation(record, entry.Offset, entry.Length) { SourcePath = path });
            }

            return locations;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static long Distance(long left, long right)
    {
        var difference = left - right;
        return difference == long.MinValue ? long.MaxValue : Math.Abs(difference);
    }

    private sealed record IndexedAnchorCandidate(HistoryAnchorLocation Location, JsonlHistoryIndexEntry Entry);

    private void InvalidateHistoryIndex(string path)
    {
        lock (_historyIndexGate)
        {
            _historyIndexes.Remove(path);
        }

        try
        {
            var sidecarPath = JsonlHistoryIndex.GetSidecarPath(path);
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }
        }
        catch (IOException)
        {
            SetDiagnostic("Conversation history index cleanup was deferred safely.");
        }
        catch (UnauthorizedAccessException)
        {
            SetDiagnostic("Conversation history index cleanup was deferred safely.");
        }
    }

    private void InvalidateSearchIndex(string path)
    {
        lock (_searchIndexGate)
        {
            _searchIndexes.Remove(path);
        }

        try
        {
            var sidecarPath = JsonlSearchIndex.GetSidecarPath(path);
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }
        }
        catch (IOException)
        {
            SetDiagnostic("Conversation search index cleanup was deferred safely.");
        }
        catch (UnauthorizedAccessException)
        {
            SetDiagnostic("Conversation search index cleanup was deferred safely.");
        }
    }
}

internal sealed class SearchResultCollector
{
    private readonly PriorityQueue<Candidate, (long TimestampTicks, long DurableSequence, long Sequence)> _candidates = new();
    private readonly HashSet<string> _serverIdentities = new(StringComparer.Ordinal);
    private readonly int _maximumCandidates;
    private long _sequence;

    public SearchResultCollector(int maximumCandidates)
    {
        _maximumCandidates = Math.Max(1, maximumCandidates);
    }

    public long MatchingRecords { get; private set; }

    public IReadOnlyList<ConversationLogSearchResult> Results => _candidates.UnorderedItems
        .Select(item => item.Element)
        .OrderByDescending(item => item.Result.Record, Comparer<ConversationLogRecord>.Create(ConversationHistoryOrdering.Compare))
        .ThenByDescending(item => item.Sequence)
        .Select(item => item.Result)
        .ToArray();

    public void Add(ConversationLogSearchResult result)
    {
        var identity = ConversationEntryIdentity.GetServerIdentityKey(result.Record);
        if (identity is not null && !_serverIdentities.Add(identity))
        {
            return;
        }

        MatchingRecords++;
        var candidate = new Candidate(result, _sequence++);
        _candidates.Enqueue(candidate, (result.Timestamp.UtcTicks, result.Record.DurableSequence, candidate.Sequence));
        if (_candidates.Count > _maximumCandidates)
        {
            _candidates.Dequeue();
        }
    }

    private sealed record Candidate(ConversationLogSearchResult Result, long Sequence);
}

internal static class HistoryPageSelector
{
    internal static bool MatchesHistoryRecord(ConversationLogRecord record, HistoryPageRequest request) =>
        record.ScopeId == request.ScopeId
        && (request.NetworkId is null || record.NetworkId == request.NetworkId)
        && record.ConversationKind == request.ConversationKind
        && (string.IsNullOrWhiteSpace(request.ConversationKey)
            ? IrcIdentity.Equals(record.ConversationName, request.ConversationName, IrcCaseMapping.Rfc1459)
            : string.Equals(ConversationLoggingService.EffectiveConversationKey(record), request.ConversationKey, StringComparison.Ordinal));

    internal static bool MatchesHistoryRecord(ConversationLogRecord record, HistoryExportRequest request) =>
        record.ScopeId == request.ScopeId
        && (request.NetworkId is null || record.NetworkId == request.NetworkId)
        && record.ConversationKind == request.ConversationKind
        && (string.IsNullOrWhiteSpace(request.ConversationKey)
            ? IrcIdentity.Equals(record.ConversationName, request.ConversationName, IrcCaseMapping.Rfc1459)
            : string.Equals(ConversationLoggingService.EffectiveConversationKey(record), request.ConversationKey, StringComparison.Ordinal));

    public static HistoryPage Select(IEnumerable<ConversationLogRecord> source, HistoryPageRequest request)
    {
        var matching = ConversationHistoryMerge.DeduplicateExact(source
            .Where(record => record.ConversationKind == request.ConversationKind
                && MatchesHistoryRecord(record, request)))
            .ToArray();
        if (matching.Length == 0)
        {
            return HistoryPage.Empty;
        }

        var pageSize = Math.Clamp(request.PageSize, 1, ConfigurationLimits.MaximumHistoryPageSize);
        var eligible = matching.Where(record => request.Around is not null
                ? record.Timestamp >= request.Around.Value - request.AroundWindow && record.Timestamp <= request.Around.Value + request.AroundWindow
                : IsBefore(record, request) && IsAfter(record, request))
            .ToArray();
        var selected = request.Around is not null
            ? eligible.OrderBy(record => Math.Abs((record.Timestamp - request.Around.Value).Ticks))
                .ThenBy(record => record, Comparer<ConversationLogRecord>.Create(ConversationHistoryOrdering.Compare))
                .Take(pageSize).ToArray()
            : request.After is not null || request.Oldest
                ? ConversationHistoryOrdering.OrderAscending(eligible).Take(pageSize).ToArray()
                : ConversationHistoryOrdering.OrderDescending(eligible).Take(pageSize).ToArray();
        var records = ConversationHistoryOrdering.OrderDescending(selected).ToArray();
        if (records.Length == 0)
        {
            return HistoryPage.Empty;
        }

        var oldest = matching.Min(record => record.Timestamp);
        var newest = matching.Max(record => record.Timestamp);
        return new HistoryPage(records, oldest < records[^1].Timestamp, newest > records[0].Timestamp, records[^1].Timestamp, records[0].Timestamp);
    }

    internal static bool IsBefore(JsonlHistoryIndexEntry entry, HistoryPageRequest request) =>
        request.Before is null
            ? true
            : entry.TimestampTicks < request.Before.Value.UtcTicks
                || entry.TimestampTicks == request.Before.Value.UtcTicks
                    && request.BeforeDurableSequence is long sequence
                    && entry.DurableSequence > 0
                    && entry.DurableSequence < sequence;

    internal static bool IsAfter(JsonlHistoryIndexEntry entry, HistoryPageRequest request) =>
        request.After is null
            ? true
            : entry.TimestampTicks > request.After.Value.UtcTicks
                || entry.TimestampTicks == request.After.Value.UtcTicks
                    && request.AfterDurableSequence is long sequence
                    && entry.DurableSequence > 0
                    && entry.DurableSequence > sequence;

    internal static bool IsBefore(ConversationLogRecord record, HistoryPageRequest request) =>
        request.Before is null
            ? true
            : record.Timestamp < request.Before.Value
                || record.Timestamp == request.Before.Value
                    && request.BeforeDurableSequence is long sequence
                    && record.DurableSequence > 0
                    && record.DurableSequence < sequence;

    internal static bool IsAfter(ConversationLogRecord record, HistoryPageRequest request) =>
        request.After is null
            ? true
            : record.Timestamp > request.After.Value
                || record.Timestamp == request.After.Value
                    && request.AfterDurableSequence is long sequence
                    && record.DurableSequence > 0
                    && record.DurableSequence > sequence;
}

public sealed class InMemoryConversationLogStore : IConversationLogStore
{
    private readonly object _gate = new();
    private readonly List<ConversationLogRecord> _records = [];
    private readonly Dictionary<string, long> _nextDurableSequences = new(StringComparer.Ordinal);
    public IReadOnlyList<ConversationLogRecord> Records { get { lock (_gate) return _records.ToArray(); } }

    public ValueTask<bool> AppendAsync(ConversationLogRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var identity = ConversationEntryIdentity.GetServerIdentityKey(record);
            if (identity is not null && _records.Any(existing => string.Equals(
                    ConversationEntryIdentity.GetServerIdentityKey(existing), identity, StringComparison.Ordinal)))
            {
                return ValueTask.FromResult(true);
            }

            var sequenceKey = $"{record.NetworkId:N}\0{ConversationLoggingService.EffectiveConversationKey(record)}";
            var next = _nextDurableSequences.TryGetValue(sequenceKey, out var known)
                ? known
                : _records.Where(existing => string.Equals(
                        $"{existing.NetworkId:N}\0{ConversationLoggingService.EffectiveConversationKey(existing)}",
                        sequenceKey,
                        StringComparison.Ordinal))
                    .Select(existing => existing.DurableSequence)
                    .DefaultIfEmpty()
                    .Max();
            var sequence = record.DurableSequence > 0 ? record.DurableSequence : checked(next + 1);
            _nextDurableSequences[sequenceKey] = Math.Max(next, sequence);
            _records.Add(record with
            {
                SchemaVersion = ConversationHistorySchema.CurrentVersion,
                DurableSequence = sequence,
                ServerMessageId = ConversationEntryIdentity.NormalizeServerMessageId(record.ServerMessageId)
            });
        }
        return ValueTask.FromResult(true);
    }

    public ValueTask<IReadOnlyList<ConversationLogRecord>> ReadPageAsync(Guid scopeId, LogConversationKind kind, string conversationName, int pageSize = ConfigurationLimits.MaximumHistoryPageSize, DateTimeOffset? before = null, CancellationToken cancellationToken = default)
    {
        return ReadPageAsyncCore(scopeId, kind, conversationName, pageSize, before, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<ConversationLogRecord>> ReadPageAsyncCore(Guid scopeId, LogConversationKind kind, string conversationName, int pageSize, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var page = await ReadPageWindowAsync(new HistoryPageRequest
        {
            ScopeId = scopeId,
            ConversationKind = kind,
            ConversationName = conversationName,
            PageSize = pageSize,
            Before = before
        }, cancellationToken).ConfigureAwait(false);
        return page.Records;
    }

    public ValueTask<HistoryPage> ReadPageWindowAsync(HistoryPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ConversationLogRecord[] records;
        lock (_gate)
        {
            records = _records.Where(record => record.ScopeId == request.ScopeId
                    && record.ConversationKind == request.ConversationKind
                    && HistoryPageSelector.MatchesHistoryRecord(record, request))
                .ToArray();
        }

        var page = HistoryPageSelector.Select(records, request);
        return ValueTask.FromResult(page);
    }

    public ValueTask<ConversationHistoryRange> ReadRangeAsync(HistoryExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var maximum = Math.Clamp(request.MaximumRecords, 1, ConfigurationLimits.MaximumHistoryExportRecords);
        lock (_gate)
        {
            var matching = ConversationHistoryMerge.DeduplicateExact(_records.Where(record => record.ScopeId == request.ScopeId
                    && record.ConversationKind == request.ConversationKind
                    && HistoryPageSelector.MatchesHistoryRecord(record, request)
                    && (request.From is null || record.Timestamp >= request.From.Value)
                    && (request.To is null || record.Timestamp <= request.To.Value)))
                .OrderBy(record => record, Comparer<ConversationLogRecord>.Create(ConversationHistoryOrdering.Compare))
                .ToArray();
            return ValueTask.FromResult(new ConversationHistoryRange(matching.Take(maximum).ToArray(), matching.Length > maximum));
        }
    }

    public ValueTask<HistoryAnchorResult> FindByServerMessageIdAsync(
        HistoryServerMessageAnchorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var messageId = ConversationEntryIdentity.NormalizeServerMessageId(request.ServerMessageId);
        if (messageId is null)
        {
            return ValueTask.FromResult(HistoryAnchorResult.Missing);
        }

        var locations = SnapshotLocations(request.Conversation)
            .Where(item => string.Equals(item.Record.ServerMessageId, messageId, StringComparison.Ordinal))
            .OrderBy(item => item, Comparer<HistoryAnchorLocation>.Create(HistoryAnchorPolicy.Compare))
            .ToArray();
        return ValueTask.FromResult(locations.Length == 0
            ? HistoryAnchorResult.Missing
            : new HistoryAnchorResult(HistoryAnchorMatch.Exact, locations[0]));
    }

    public ValueTask<HistoryAnchorResult> FindByTimestampAsync(
        HistoryTimestampAnchorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(HistoryAnchorPolicy.SelectTimestamp(
            SnapshotLocations(request.Conversation),
            request.Timestamp,
            request.Direction));
    }

    public async ValueTask<HistoryContextResult> ReadContextAroundAsync(
        HistoryContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var locations = SnapshotLocations(request.Conversation)
            .OrderBy(item => item, Comparer<HistoryAnchorLocation>.Create(HistoryAnchorPolicy.Compare))
            .ToArray();
        HistoryAnchorResult anchor;
        if (request.CanonicalAnchor is { } canonicalAnchor)
        {
            var exact = locations.FirstOrDefault(item =>
                item.SourceOffset == canonicalAnchor.SourceOffset
                && item.Record.Timestamp == canonicalAnchor.Record.Timestamp
                && item.Record.DurableSequence == canonicalAnchor.Record.DurableSequence
                && string.Equals(item.Record.Text, canonicalAnchor.Record.Text, StringComparison.Ordinal));
            anchor = exact is null
                ? HistoryAnchorResult.Missing
                : new HistoryAnchorResult(HistoryAnchorMatch.Exact, exact);
        }
        else if (request.ServerMessageId is { Length: > 0 } messageId)
        {
            var normalized = ConversationEntryIdentity.NormalizeServerMessageId(messageId);
            var exact = locations.FirstOrDefault(item => string.Equals(item.Record.ServerMessageId, normalized, StringComparison.Ordinal));
            anchor = exact is null
                ? HistoryAnchorResult.Missing
                : new HistoryAnchorResult(HistoryAnchorMatch.Exact, exact);
        }
        else if (request.Timestamp is { } timestamp)
        {
            anchor = HistoryAnchorPolicy.SelectTimestamp(locations, timestamp, request.TimestampDirection);
        }
        else
        {
            return HistoryContextResult.Missing;
        }

        if (!anchor.Found || anchor.Anchor is null)
        {
            return new HistoryContextResult(anchor, Array.Empty<ConversationLogRecord>(), false);
        }

        var anchorIndex = Array.FindIndex(locations, item => item.SourceOffset == anchor.Anchor.SourceOffset);
        if (anchorIndex < 0)
        {
            return new HistoryContextResult(anchor, Array.Empty<ConversationLogRecord>(), false);
        }

        var before = Math.Clamp(request.BeforeCount, 0, ConfigurationLimits.MaximumHistoryContextEntries);
        var after = Math.Clamp(request.AfterCount, 0, ConfigurationLimits.MaximumHistoryContextEntries);
        var first = Math.Max(0, anchorIndex - before);
        var last = Math.Min(locations.Length - 1, anchorIndex + after);
        var records = locations[first..(last + 1)]
            .Select(item => item.Record)
            .ToArray();
        return await Task.FromResult(new HistoryContextResult(anchor, records, true)).ConfigureAwait(false);
    }

    private HistoryAnchorLocation[] SnapshotLocations(HistoryConversationAddress address)
    {
        lock (_gate)
        {
            return _records
                .Select((record, index) => (record, index))
                .Where(item => HistoryAnchorPolicy.IsAddressMatch(item.record, address))
                .Select(item => new HistoryAnchorLocation(item.record, item.index, 0) { SourcePath = "memory" })
                .ToArray();
        }
    }

    public ValueTask<IReadOnlyList<ConversationLogSearchResult>> SearchAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        return SearchAsyncCore(query, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<ConversationLogSearchResult>> SearchAsyncCore(ConversationLogQuery query, CancellationToken cancellationToken)
    {
        var page = await SearchDetailedAsync(query, cancellationToken).ConfigureAwait(false);
        return page.Results;
    }

    public ValueTask<ConversationLogSearchPage> SearchDetailedAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HistorySearchRequestValidator.TryNormalize(query, out var normalizedQuery, out var validationError))
        {
            return ValueTask.FromResult(new ConversationLogSearchPage(
                Array.Empty<ConversationLogSearchResult>(),
                new ConversationLogSearchStatistics(0, 0, 0, 0, 0, false, false, ValidationError: validationError)));
        }

        query = normalizedQuery;
        var text = query.Text ?? string.Empty;
        var maximum = query.MaximumResults;
        var skip = query.Skip;
        var collector = new SearchResultCollector(Math.Min(ConfigurationLimits.MaximumSearchResults * 2, maximum + skip));
        var examined = 0L;
        foreach (var record in Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            if (JsonlConversationLogStore.Matches(record, query, text))
            {
                collector.Add(new ConversationLogSearchResult(record, Preview(record.Text, text)));
            }
        }

        var results = collector.Results.Skip(skip).Take(maximum).ToArray();
        return ValueTask.FromResult(new ConversationLogSearchPage(
            results,
            new ConversationLogSearchStatistics(
                examined,
                0,
                0,
                collector.MatchingRecords,
                results.Length,
                collector.MatchingRecords > (long)skip + maximum,
                false)));
    }

    private static string Preview(string text, string query) =>
        text.Length > ConfigurationLimits.MaximumSearchSnippetLength
            ? text[..ConfigurationLimits.MaximumSearchSnippetLength]
            : text;

    public ValueTask<int> CleanupAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var count = _records.RemoveAll(record => record.Timestamp < olderThan);
            _nextDurableSequences.Clear();
            return ValueTask.FromResult(count);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
