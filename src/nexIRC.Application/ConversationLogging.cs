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

public interface IConversationLogStore : IAsyncDisposable
{
    ValueTask<bool> AppendAsync(ConversationLogRecord record, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ConversationLogRecord>> ReadPageAsync(Guid scopeId, LogConversationKind kind, string conversationName, int pageSize = ConfigurationLimits.MaximumHistoryPageSize, DateTimeOffset? before = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ConversationLogSearchResult>> SearchAsync(ConversationLogQuery query, CancellationToken cancellationToken = default);
    ValueTask<int> CleanupAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class ConversationLoggingService
{
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
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        pageSize = Math.Clamp(pageSize, 1, ConfigurationLimits.MaximumHistoryPageSize);
        var path = GetPath(scopeId, ConversationLoggingService.BuildConversationKey(kind, conversationName));
        if (!File.Exists(path))
        {
            return Array.Empty<ConversationLogRecord>();
        }

        var records = new List<ConversationLogRecord>(pageSize);
        try
        {
            await foreach (var record in ReadFileAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (record.ConversationKind != kind || !IrcIdentity.Equals(record.ConversationName, conversationName, IrcCaseMapping.Rfc1459) || before is not null && record.Timestamp >= before.Value)
                {
                    continue;
                }

                records.Add(record);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetDiagnostic($"Conversation history read failed safely: {exception.Message}");
        }

        return records.OrderByDescending(record => record.Timestamp).Take(pageSize).ToArray();
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
        if (new FileInfo(path).Length > 16 * 1024 * 1024) yield break;
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

    private void SetDiagnostic(string diagnostic) { lock (_diagnosticGate) _lastDiagnostic = diagnostic; }
    private sealed record PendingWrite(ConversationLogRecord? Record, TaskCompletionSource<bool> Completion);
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
        pageSize = Math.Clamp(pageSize, 1, ConfigurationLimits.MaximumHistoryPageSize);
        lock (_gate)
        {
            var result = _records.Where(record => record.ScopeId == scopeId && record.ConversationKind == kind && IrcIdentity.Equals(record.ConversationName, conversationName, IrcCaseMapping.Rfc1459) && (before is null || record.Timestamp < before.Value))
                .OrderByDescending(record => record.Timestamp).Take(pageSize).ToArray();
            return ValueTask.FromResult<IReadOnlyList<ConversationLogRecord>>(result);
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
