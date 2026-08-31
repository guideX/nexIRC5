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

public sealed record ConversationLogRecord
{
    public DateTimeOffset Timestamp { get; init; }
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
}

public sealed record ConversationLogQuery
{
    public string? Text { get; init; }
    public Guid? NetworkId { get; init; }
    public Guid? ProfileId { get; init; }
    public LogConversationKind? ConversationKind { get; init; }
    public string? ConversationName { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int MaximumResults { get; init; } = 100;
    public int Skip { get; init; }
}

public sealed record ConversationLogSearchResult(ConversationLogRecord Record, string Preview);

public sealed record HistoryPageRequest
{
    public required Guid ScopeId { get; init; }
    public required LogConversationKind ConversationKind { get; init; }
    public required string ConversationName { get; init; }
    public int PageSize { get; init; } = ConfigurationLimits.MaximumHistoryPageSize;
    public DateTimeOffset? Before { get; init; }
    public DateTimeOffset? After { get; init; }
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
    public required LogConversationKind ConversationKind { get; init; }
    public required string ConversationName { get; init; }
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
    ValueTask<IReadOnlyList<ConversationLogSearchResult>> SearchAsync(ConversationLogQuery query, CancellationToken cancellationToken = default);
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
        var text = RedactConversationText(entry.Text);
        var record = new ConversationLogRecord
        {
            Timestamp = entry.Timestamp,
            NetworkId = networkId,
            ScopeId = profileId ?? networkId,
            ProfileId = profileId,
            ConversationKind = kind,
            ConversationName = name,
            ConversationKey = BuildConversationKey(kind, name),
            Sender = entry.Sender,
            MessageKind = ToLogKind(entry.Kind),
            Direction = entry.IsOutgoing ? LogDirection.Outgoing : LogDirection.Incoming,
            Text = text,
            IsHighlight = entry.IsHighlight
        };
        _ = AppendSafeAsync(record);
    }

    public static string BuildConversationKey(LogConversationKind kind, string name) =>
        $"{kind}:{IrcCaseMappingComparer.Fold(name, IrcCaseMapping.Rfc1459)}";

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
                foreach (var record in range.Records.OrderBy(record => record.Timestamp))
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
    private string? _lastDiagnostic;
    private int _disposed;

    public JsonlConversationLogStore(string root, int maximumRecordBytes = ConfigurationLimits.MaximumLogRecordBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _maximumRecordBytes = Math.Max(1024, maximumRecordBytes);
        _writer = Task.Run(WriterAsync);
    }

    public string RootPath => _root;
    public string? LastDiagnostic { get { lock (_diagnosticGate) return _lastDiagnostic; } }

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
        var path = GetPath(request.ScopeId, ConversationLoggingService.BuildConversationKey(request.ConversationKind, request.ConversationName));
        if (!File.Exists(path))
        {
            return HistoryPage.Empty;
        }

        if (await TryReadIndexedPageAsync(path, request, pageSize, cancellationToken).ConfigureAwait(false) is { } indexedPage)
        {
            return indexedPage;
        }

        var selected = new List<ConversationLogRecord>(pageSize + 1);
        DateTimeOffset? oldest = null;
        DateTimeOffset? newest = null;
        var preferOldest = (request.After is not null || request.Oldest) && request.Around is null;
        try
        {
            await foreach (var record in ReadFileAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (record.ScopeId != request.ScopeId || record.ConversationKind != request.ConversationKind || !IrcIdentity.Equals(record.ConversationName, request.ConversationName, IrcCaseMapping.Rfc1459))
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

                if (request.Around is not null)
                {
                    selected.Add(record);
                    selected.Sort((left, right) => CompareAround(left, right, request.Around.Value));
                    if (selected.Count > pageSize) selected.RemoveAt(selected.Count - 1);
                }
                else
                {
                    selected.Add(record);
                    selected.Sort(preferOldest ? CompareAscending : CompareDescending);
                    if (selected.Count > pageSize) selected.RemoveAt(selected.Count - 1);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetDiagnostic($"Conversation history read failed safely: {exception.Message}");
        }

        var records = selected.OrderByDescending(record => record.Timestamp).ToArray();
        if (records.Length == 0)
        {
            return HistoryPage.Empty;
        }

        var pageOldest = records[^1].Timestamp;
        var pageNewest = records[0].Timestamp;
        return new HistoryPage(records, oldest < pageOldest, newest > pageNewest, pageOldest, pageNewest);
    }

    public async ValueTask<ConversationHistoryRange> ReadRangeAsync(HistoryExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var maximum = Math.Clamp(request.MaximumRecords, 1, ConfigurationLimits.MaximumHistoryExportRecords);
        var path = GetPath(request.ScopeId, ConversationLoggingService.BuildConversationKey(request.ConversationKind, request.ConversationName));
        if (!File.Exists(path))
        {
            return new ConversationHistoryRange(Array.Empty<ConversationLogRecord>(), false);
        }

        var index = await GetHistoryIndexAsync(path, request.ScopeId, request.ConversationKind, request.ConversationName, cancellationToken).ConfigureAwait(false);
        if (index is not null)
        {
            var indexedEntries = index.Entries
                .Where(entry => request.From is null || entry.TimestampTicks >= request.From.Value.UtcTicks)
                .Where(entry => request.To is null || entry.TimestampTicks <= request.To.Value.UtcTicks)
                .ToArray();
            var maximumIndexed = Math.Min(maximum, indexedEntries.Length);
            var indexedRecords = await ReadIndexedRecordsAsync(
                path,
                indexedEntries.Take(maximumIndexed),
                request.ScopeId,
                request.ConversationKind,
                request.ConversationName,
                cancellationToken).ConfigureAwait(false);
            if (indexedRecords is not null)
            {
                return new ConversationHistoryRange(
                    indexedRecords.OrderBy(record => record.Timestamp).ToArray(),
                    indexedEntries.Length > maximum);
            }
        }

        var records = new List<ConversationLogRecord>(Math.Min(maximum, ConfigurationLimits.MaximumHistoryPageSize));
        var truncated = false;
        try
        {
            await foreach (var record in ReadFileAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (record.ScopeId != request.ScopeId
                    || record.ConversationKind != request.ConversationKind
                    || !IrcIdentity.Equals(record.ConversationName, request.ConversationName, IrcCaseMapping.Rfc1459)
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

        return new ConversationHistoryRange(records.OrderBy(record => record.Timestamp).ToArray(), truncated);
    }

    public async ValueTask<IReadOnlyList<ConversationLogSearchResult>> SearchAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var text = query.Text?.Trim() ?? string.Empty;
        if (text.Length > ConfigurationLimits.MaximumSearchQueryLength)
        {
            return Array.Empty<ConversationLogSearchResult>();
        }

        var maximum = Math.Clamp(query.MaximumResults, 1, ConfigurationLimits.MaximumSearchResults);
        var skip = Math.Clamp(query.Skip, 0, ConfigurationLimits.MaximumSearchResults);
        var candidateLimit = Math.Min(ConfigurationLimits.MaximumSearchResults * 2, maximum + skip);
        var results = new List<ConversationLogSearchResult>(candidateLimit);
        if (!Directory.Exists(_root))
        {
            return results;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).Take(4096))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await foreach (var record in ReadFileAsync(path, cancellationToken).ConfigureAwait(false))
                    {
                        if (!Matches(record, query, text))
                        {
                            continue;
                        }

                        results.Add(new ConversationLogSearchResult(record, Preview(record.Text, text)));
                        if (results.Count > candidateLimit)
                        {
                            var oldest = results.MinBy(result => result.Record.Timestamp);
                            if (oldest is not null)
                            {
                                results.Remove(oldest);
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    SetDiagnostic($"Conversation log search skipped an unreadable file safely: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetDiagnostic($"Conversation log search failed safely: {exception.Message}");
        }

        return results
            .OrderByDescending(result => result.Record.Timestamp)
            .Skip(skip)
            .Take(maximum)
            .ToArray();
    }

    public async ValueTask<int> CleanupAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        foreach (var path in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).Take(4096))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retained = new List<ConversationLogRecord>();
            await foreach (var record in ReadFileAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (record.Timestamp >= olderThan) retained.Add(record); else removed++;
            }

            if (retained.Count == 0)
            {
                File.Delete(path);
                InvalidateHistoryIndex(path);
                continue;
            }

            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                {
                    await using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    foreach (var record in retained)
                    {
                        var line = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, true);
                InvalidateHistoryIndex(path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) try { File.Delete(temporaryPath); } catch { }
            }
        }

        return removed;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new PendingWrite(null, completion))) return;
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
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
                    var path = GetPath(pending.Record.ScopeId, pending.Record.ConversationKey);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var line = JsonSerializer.SerializeToUtf8Bytes(pending.Record, JsonOptions);
                    await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await stream.WriteAsync(line).ConfigureAwait(false);
                    await stream.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    lock (_historyIndexGate)
                    {
                        _historyIndexes.Remove(path);
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

    private async IAsyncEnumerable<ConversationLogRecord> ReadFileAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > ConfigurationLimits.MaximumHistoryFileBytes) yield break;
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > _maximumRecordBytes) continue;
            ConversationLogRecord? record;
            try { record = JsonSerializer.Deserialize<ConversationLogRecord>(line, JsonOptions); } catch (JsonException) { continue; }
            if (record is not null && record.Text.Length <= ConfigurationLimits.MaximumLogRecordBytes) yield return record;
        }
    }

    private string GetPath(Guid scopeId, string conversationKey)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(conversationKey)))[..24].ToLowerInvariant();
        return Path.Combine(_root, scopeId.ToString("N"), $"{hash}.jsonl");
    }

    private static bool Matches(ConversationLogRecord record, ConversationLogQuery query, string text) =>
        (string.IsNullOrEmpty(text) || record.Text.Contains(text, StringComparison.OrdinalIgnoreCase) || record.Sender?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
        && (query.NetworkId is null || record.NetworkId == query.NetworkId)
        && (query.ProfileId is null || record.ProfileId == query.ProfileId || record.ScopeId == query.ProfileId)
        && (query.ConversationKind is null || record.ConversationKind == query.ConversationKind)
        && (string.IsNullOrWhiteSpace(query.ConversationName) || IrcIdentity.Equals(record.ConversationName, query.ConversationName, IrcCaseMapping.Rfc1459))
        && (query.From is null || record.Timestamp >= query.From)
        && (query.To is null || record.Timestamp <= query.To);

    private static string Preview(string text, string query)
    {
        const int maximum = 240;
        var index = string.IsNullOrEmpty(query) ? 0 : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = index > 80 ? index - 80 : 0;
        var preview = text[start..Math.Min(text.Length, start + maximum)];
        return start > 0 ? "…" + preview : preview;
    }

    private static int CompareDescending(ConversationLogRecord left, ConversationLogRecord right)
    {
        var timestamp = right.Timestamp.CompareTo(left.Timestamp);
        return timestamp != 0 ? timestamp : right.ConversationName.CompareTo(left.ConversationName, StringComparison.Ordinal);
    }

    private static int CompareAscending(ConversationLogRecord left, ConversationLogRecord right)
    {
        var timestamp = left.Timestamp.CompareTo(right.Timestamp);
        return timestamp != 0 ? timestamp : left.ConversationName.CompareTo(right.ConversationName, StringComparison.Ordinal);
    }

    private static int CompareAround(ConversationLogRecord left, ConversationLogRecord right, DateTimeOffset around)
    {
        var leftDistance = Math.Abs((left.Timestamp - around).Ticks);
        var rightDistance = Math.Abs((right.Timestamp - around).Ticks);
        var distance = leftDistance.CompareTo(rightDistance);
        return distance != 0 ? distance : CompareDescending(left, right);
    }

    private void SetDiagnostic(string diagnostic) { lock (_diagnosticGate) _lastDiagnostic = diagnostic; }
    private sealed record PendingWrite(ConversationLogRecord? Record, TaskCompletionSource<bool> Completion);

    private async Task<HistoryPage?> TryReadIndexedPageAsync(
        string path,
        HistoryPageRequest request,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var index = await GetHistoryIndexAsync(path, request.ScopeId, request.ConversationKind, request.ConversationName, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            return null;
        }

        var eligible = index.Entries.Where(entry => request.Around is not null
                ? entry.TimestampTicks >= request.Around.Value.UtcTicks - request.AroundWindow.Ticks
                    && entry.TimestampTicks <= request.Around.Value.UtcTicks + request.AroundWindow.Ticks
                : (request.Before is null || entry.TimestampTicks < request.Before.Value.UtcTicks)
                    && (request.After is null || entry.TimestampTicks > request.After.Value.UtcTicks))
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
            cancellationToken).ConfigureAwait(false);
        if (loaded is null || loaded.Count != selected.Length)
        {
            return null;
        }

        var records = loaded.OrderByDescending(record => record.Timestamp).ToArray();
        var oldest = index.Entries.Min(entry => entry.TimestampTicks);
        var newest = index.Entries.Max(entry => entry.TimestampTicks);
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
        CancellationToken cancellationToken)
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

            lock (_historyIndexGate)
            {
                if (_historyIndexes.TryGetValue(path, out var cached)
                    && cached.SourceLength == sourceInfo.Length
                    && cached.SourceLastWriteTicks == sourceInfo.LastWriteTimeUtc.Ticks)
                {
                    return cached;
                }
            }

            var index = await JsonlHistoryIndex.LoadOrBuildAsync(
                path,
                scopeId,
                conversationKind,
                conversationName,
                _maximumRecordBytes,
                cancellationToken).ConfigureAwait(false);
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
                if (record is null
                    || record.Text.Length > ConfigurationLimits.MaximumLogRecordBytes
                    || record.ScopeId != scopeId
                    || record.ConversationKind != conversationKind
                    || !IrcIdentity.Equals(record.ConversationName, conversationName, IrcCaseMapping.Rfc1459)
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

    private static long Distance(long left, long right)
    {
        var difference = left - right;
        return difference == long.MinValue ? long.MaxValue : Math.Abs(difference);
    }

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
}

internal static class HistoryPageSelector
{
    public static HistoryPage Select(IEnumerable<ConversationLogRecord> source, HistoryPageRequest request)
    {
        var matching = source
            .Where(record => record.ConversationKind == request.ConversationKind
                && IrcIdentity.Equals(record.ConversationName, request.ConversationName, IrcCaseMapping.Rfc1459))
            .ToArray();
        if (matching.Length == 0)
        {
            return HistoryPage.Empty;
        }

        var pageSize = Math.Clamp(request.PageSize, 1, ConfigurationLimits.MaximumHistoryPageSize);
        var eligible = matching.Where(record => request.Around is not null
                ? record.Timestamp >= request.Around.Value - request.AroundWindow && record.Timestamp <= request.Around.Value + request.AroundWindow
                : (request.Before is null || record.Timestamp < request.Before.Value)
                    && (request.After is null || record.Timestamp > request.After.Value))
            .ToArray();
        var selected = request.Around is not null
            ? eligible.OrderBy(record => Math.Abs((record.Timestamp - request.Around.Value).Ticks)).ThenByDescending(record => record.Timestamp).Take(pageSize).ToArray()
            : request.After is not null || request.Oldest
                ? eligible.OrderBy(record => record.Timestamp).Take(pageSize).ToArray()
                : eligible.OrderByDescending(record => record.Timestamp).Take(pageSize).ToArray();
        var records = selected.OrderByDescending(record => record.Timestamp).ToArray();
        if (records.Length == 0)
        {
            return HistoryPage.Empty;
        }

        var oldest = matching.Min(record => record.Timestamp);
        var newest = matching.Max(record => record.Timestamp);
        return new HistoryPage(records, oldest < records[^1].Timestamp, newest > records[0].Timestamp, records[^1].Timestamp, records[0].Timestamp);
    }
}

public sealed class InMemoryConversationLogStore : IConversationLogStore
{
    private readonly object _gate = new();
    private readonly List<ConversationLogRecord> _records = [];
    public IReadOnlyList<ConversationLogRecord> Records { get { lock (_gate) return _records.ToArray(); } }

    public ValueTask<bool> AppendAsync(ConversationLogRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _records.Add(record);
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
                    && IrcIdentity.Equals(record.ConversationName, request.ConversationName, IrcCaseMapping.Rfc1459))
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
            var matching = _records.Where(record => record.ScopeId == request.ScopeId
                    && record.ConversationKind == request.ConversationKind
                    && IrcIdentity.Equals(record.ConversationName, request.ConversationName, IrcCaseMapping.Rfc1459)
                    && (request.From is null || record.Timestamp >= request.From.Value)
                    && (request.To is null || record.Timestamp <= request.To.Value))
                .OrderBy(record => record.Timestamp)
                .ToArray();
            return ValueTask.FromResult(new ConversationHistoryRange(matching.Take(maximum).ToArray(), matching.Length > maximum));
        }
    }

    public ValueTask<IReadOnlyList<ConversationLogSearchResult>> SearchAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Text?.Trim() ?? string.Empty;
        if (text.Length > ConfigurationLimits.MaximumSearchQueryLength)
        {
            return ValueTask.FromResult<IReadOnlyList<ConversationLogSearchResult>>(Array.Empty<ConversationLogSearchResult>());
        }

        var result = Records.Where(record => (string.IsNullOrEmpty(text) || record.Text.Contains(text, StringComparison.OrdinalIgnoreCase) || record.Sender?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                && (query.NetworkId is null || record.NetworkId == query.NetworkId)
                && (query.ProfileId is null || record.ProfileId == query.ProfileId || record.ScopeId == query.ProfileId)
                && (query.ConversationKind is null || record.ConversationKind == query.ConversationKind)
                && (string.IsNullOrWhiteSpace(query.ConversationName) || IrcIdentity.Equals(record.ConversationName, query.ConversationName, IrcCaseMapping.Rfc1459))
                && (query.From is null || record.Timestamp >= query.From)
                && (query.To is null || record.Timestamp <= query.To))
            .OrderByDescending(record => record.Timestamp)
            .Skip(Math.Clamp(query.Skip, 0, ConfigurationLimits.MaximumSearchResults))
            .Take(Math.Clamp(query.MaximumResults, 1, ConfigurationLimits.MaximumSearchResults))
            .Select(record => new ConversationLogSearchResult(record, record.Text.Length > 240 ? record.Text[..240] : record.Text))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<ConversationLogSearchResult>>(result);
    }

    public ValueTask<int> CleanupAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        lock (_gate) { var count = _records.RemoveAll(record => record.Timestamp < olderThan); return ValueTask.FromResult(count); }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
