using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using nexIRC.Core.State;

namespace nexIRC.Application;

internal sealed record JsonlHistoryIndexEntry(long TimestampTicks, long Offset, int Length);

internal sealed record JsonlHistoryIndexSnapshot(
    long SourceLength,
    long SourceLastWriteTicks,
    IReadOnlyList<JsonlHistoryIndexEntry> Entries,
    long SidecarLength = 0,
    long SidecarLastWriteTicks = 0);

/// <summary>
/// A small, disposable acceleration structure for JSONL navigation. It stores
/// only source-file metadata, timestamps, and byte ranges; the JSONL file
/// remains the authoritative record and the sidecar can always be rebuilt.
/// </summary>
internal static class JsonlHistoryIndex
{
    private static readonly byte[] Magic = "NEXHIDX1"u8.ToArray();
    private const int FormatVersion = 1;
    private const int EntryBytes = sizeof(long) + sizeof(long) + sizeof(int);
    private const int HeaderBytes = 8 + sizeof(int) + sizeof(long) + sizeof(long) + sizeof(int);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 12,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<JsonlHistoryIndexSnapshot?> LoadOrBuildAsync(
        string sourcePath,
        Guid scopeId,
        LogConversationKind conversationKind,
        string conversationName,
        int maximumRecordBytes,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists
            || sourceInfo.Length < ConfigurationLimits.MinimumHistoryIndexFileBytes
            || sourceInfo.Length > ConfigurationLimits.MaximumHistoryFileBytes)
        {
            return null;
        }

        var sourceLength = sourceInfo.Length;
        var sourceLastWriteTicks = sourceInfo.LastWriteTimeUtc.Ticks;
        var sidecarPath = GetSidecarPath(sourcePath);
        if (TryRead(sidecarPath, sourceLength, sourceLastWriteTicks, out var existing))
        {
            return existing;
        }

        var entries = await BuildAsync(
            sourcePath,
            scopeId,
            conversationKind,
            conversationName,
            maximumRecordBytes,
            cancellationToken).ConfigureAwait(false);
        if (entries is null)
        {
            return null;
        }

        var afterBuild = new FileInfo(sourcePath);
        if (!afterBuild.Exists || afterBuild.Length != sourceLength || afterBuild.LastWriteTimeUtc.Ticks != sourceLastWriteTicks)
        {
            return null;
        }

        var snapshot = new JsonlHistoryIndexSnapshot(sourceLength, sourceLastWriteTicks, entries);
        try
        {
            await WriteAsync(sidecarPath, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The sidecar is disposable. Navigation can continue using JSONL.
        }

        try
        {
            var sidecarInfo = new FileInfo(sidecarPath);
            return sidecarInfo.Exists
                ? snapshot with { SidecarLength = sidecarInfo.Length, SidecarLastWriteTicks = sidecarInfo.LastWriteTimeUtc.Ticks }
                : snapshot;
        }
        catch (IOException)
        {
            return snapshot;
        }
        catch (UnauthorizedAccessException)
        {
            return snapshot;
        }
    }

    public static string GetSidecarPath(string sourcePath) => $"{sourcePath}.hidx";

    private static async Task<IReadOnlyList<JsonlHistoryIndexEntry>?> BuildAsync(
        string path,
        Guid scopeId,
        LogConversationKind conversationKind,
        string conversationName,
        int maximumRecordBytes,
        CancellationToken cancellationToken)
    {
        var entries = new List<JsonlHistoryIndexEntry>();
        await foreach (var line in ReadLinesWithOffsetsAsync(path, maximumRecordBytes, cancellationToken).ConfigureAwait(false))
        {
            ConversationLogRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ConversationLogRecord>(line.Bytes, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (!ConversationLogRecordValidation.IsReadable(record)
                || record!.Text.Length > ConfigurationLimits.MaximumLogRecordBytes
                || record.ScopeId != scopeId
                || record.ConversationKind != conversationKind
                || !IrcCaseMappingComparer.Equals(record.ConversationName, conversationName, IrcCaseMapping.Rfc1459))
            {
                continue;
            }

            if (entries.Count >= ConfigurationLimits.MaximumHistoryIndexEntries)
            {
                return null;
            }

            entries.Add(new JsonlHistoryIndexEntry(record.Timestamp.UtcTicks, line.Offset, line.Bytes.Length));
        }

        return entries;
    }

    private static bool TryRead(
        string path,
        long sourceLength,
        long sourceLastWriteTicks,
        out JsonlHistoryIndexSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length < HeaderBytes)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[HeaderBytes];
            stream.ReadExactly(header);
            if (!header[..Magic.Length].SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != FormatVersion)
            {
                return false;
            }

            var indexedLength = BinaryPrimitives.ReadInt64LittleEndian(header[12..]);
            var indexedLastWriteTicks = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
            if (indexedLength != sourceLength
                || indexedLastWriteTicks != sourceLastWriteTicks
                || count < 0
                || count > ConfigurationLimits.MaximumHistoryIndexEntries
                || stream.Length != HeaderBytes + (long)count * EntryBytes)
            {
                return false;
            }

            var entries = new List<JsonlHistoryIndexEntry>(count);
            var entryBuffer = new byte[EntryBytes];
            long previousOffset = -1;
            for (var index = 0; index < count; index++)
            {
                stream.ReadExactly(entryBuffer);
                var timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(entryBuffer);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(entryBuffer.AsSpan(8));
                var length = BinaryPrimitives.ReadInt32LittleEndian(entryBuffer.AsSpan(16));
                if (offset <= previousOffset
                    || offset < 0
                    || length <= 0
                    || length > ConfigurationLimits.MaximumLogRecordBytes
                    || offset > sourceLength
                    || length > sourceLength - offset)
                {
                    return false;
                }

                entries.Add(new JsonlHistoryIndexEntry(timestampTicks, offset, length));
                previousOffset = offset;
            }

            var sidecarInfo = new FileInfo(path);
            snapshot = new JsonlHistoryIndexSnapshot(
                sourceLength,
                sourceLastWriteTicks,
                entries,
                stream.Length,
                sidecarInfo.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }

    internal static bool IsSidecarCurrent(string path, JsonlHistoryIndexSnapshot snapshot)
    {
        if (snapshot.SidecarLength <= 0)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists
                && info.Length == snapshot.SidecarLength
                && info.LastWriteTimeUtc.Ticks == snapshot.SidecarLastWriteTicks;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task WriteAsync(string path, JsonlHistoryIndexSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
                await WriteInt32Async(stream, FormatVersion, cancellationToken).ConfigureAwait(false);
                await WriteInt64Async(stream, snapshot.SourceLength, cancellationToken).ConfigureAwait(false);
                await WriteInt64Async(stream, snapshot.SourceLastWriteTicks, cancellationToken).ConfigureAwait(false);
                await WriteInt32Async(stream, snapshot.Entries.Count, cancellationToken).ConfigureAwait(false);
                var entryBuffer = new byte[EntryBytes];
                foreach (var entry in snapshot.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BinaryPrimitives.WriteInt64LittleEndian(entryBuffer, entry.TimestampTicks);
                    BinaryPrimitives.WriteInt64LittleEndian(entryBuffer.AsSpan(8), entry.Offset);
                    BinaryPrimitives.WriteInt32LittleEndian(entryBuffer.AsSpan(16), entry.Length);
                    await stream.WriteAsync(entryBuffer, cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Index cleanup is best effort; the authoritative JSONL was not touched.
            }
        }
    }

    private static async Task WriteInt32Async(Stream stream, int value, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteInt64Async(Stream stream, long value, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async IAsyncEnumerable<IndexedLine> ReadLinesWithOffsetsAsync(
        string path,
        int maximumRecordBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        long startOffset = 0,
        long? endOffset = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = startOffset;
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream(Math.Min(maximumRecordBytes, 64 * 1024));
        var lineOffset = startOffset;
        var sourceOffset = startOffset;
        var oversized = false;
        var reachedEnd = false;
        while (!reachedEnd && await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var read && read > 0)
        {
            for (var index = 0; index < read; index++, sourceOffset++)
            {
                if (endOffset is long end && sourceOffset >= end)
                {
                    reachedEnd = true;
                    break;
                }

                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (!oversized)
                    {
                        var bytes = line.ToArray();
                        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                        {
                            Array.Resize(ref bytes, bytes.Length - 1);
                        }

                        if (bytes.Length > 0)
                        {
                            yield return new IndexedLine(lineOffset, bytes);
                        }
                    }

                    line.SetLength(0);
                    oversized = false;
                    lineOffset = sourceOffset + 1;
                }
                else if (!oversized)
                {
                    if (line.Length >= maximumRecordBytes)
                    {
                        oversized = true;
                    }
                    else
                    {
                        line.WriteByte(value);
                    }
                }
            }
        }

        if (!reachedEnd && !oversized && line.Length > 0 && (endOffset is null || lineOffset < endOffset))
        {
            yield return new IndexedLine(lineOffset, line.ToArray());
        }
    }

    internal sealed record IndexedLine(long Offset, byte[] Bytes);
}
