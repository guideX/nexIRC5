using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nexIRC.Application;

internal sealed record JsonlSearchIndexBlock(
    long Offset,
    long Length,
    long MinimumTimestampTicks,
    long MaximumTimestampTicks,
    int RecordCount,
    byte[] Bloom,
    byte[] SourceFingerprint);

internal sealed record JsonlSearchIndexSnapshot(
    long SourceLength,
    long SourceLastWriteTicks,
    IReadOnlyList<JsonlSearchIndexBlock> Blocks,
    long SidecarLength = 0,
    long SidecarLastWriteTicks = 0);

/// <summary>
/// A deliberately coarse, disposable content-search accelerator. It stores
/// only block offsets, timestamp bounds, counts, and a Bloom filter of
/// case-folded trigrams; JSONL remains the only source of message text.
/// </summary>
internal static class JsonlSearchIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 12,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly byte[] Magic = "NEXSIDX1"u8.ToArray();
    private const int FormatVersion = 3;
    private const int HashCount = 3;
    private const int SidecarHashBytes = 32;
    private const int SourceFingerprintBytes = 32;
    private static readonly int BloomBits = ConfigurationLimits.HistorySearchIndexBloomBytes * 8;
    private static readonly int HeaderBytes = Magic.Length + sizeof(int) + sizeof(long) + sizeof(long) + sizeof(int) + sizeof(int) + sizeof(int);
    private static readonly int BlockBytes = sizeof(long) + sizeof(long) + sizeof(long) + sizeof(long) + sizeof(int) + SourceFingerprintBytes + ConfigurationLimits.HistorySearchIndexBloomBytes;

    public static string GetSidecarPath(string sourcePath) => $"{sourcePath}.hsidx";

    internal static bool TryLoad(string sourcePath, out JsonlSearchIndexSnapshot? snapshot)
    {
        var sourceInfo = new FileInfo(sourcePath);
        snapshot = null;
        return IsEligibleSource(sourceInfo)
            && TryRead(GetSidecarPath(sourcePath), sourcePath, sourceInfo.Length, sourceInfo.LastWriteTimeUtc.Ticks, out snapshot);
    }

    internal static async Task<JsonlSearchIndexSnapshot?> WriteBuiltAsync(
        string sourcePath,
        long sourceLength,
        long sourceLastWriteTicks,
        IReadOnlyList<JsonlSearchIndexBlock> blocks,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (!IsSameSource(sourceInfo, sourceLength, sourceLastWriteTicks))
        {
            return null;
        }

        IReadOnlyList<JsonlSearchIndexBlock> fingerprintedBlocks;
        try
        {
            fingerprintedBlocks = AddSourceFingerprints(sourcePath, sourceLength, sourceLastWriteTicks, blocks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }

        var snapshot = new JsonlSearchIndexSnapshot(sourceLength, sourceLastWriteTicks, fingerprintedBlocks);
        try
        {
            await WriteAsync(GetSidecarPath(sourcePath), snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return snapshot;
        }

        try
        {
            var sidecarInfo = new FileInfo(GetSidecarPath(sourcePath));
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

    /// <summary>
    /// Extends a current sidecar with one canonical append. This is best
    /// effort: any source or sidecar mismatch returns null and the next search
    /// will rebuild from JSONL. The append path never depends on this method
    /// for durable history persistence.
    /// </summary>
    internal static async Task<JsonlSearchIndexSnapshot?> TryAppendBuiltAsync(
        string sourcePath,
        JsonlSearchIndexSnapshot previous,
        ConversationLogRecord record,
        long offset,
        int sourceLength,
        CancellationToken cancellationToken)
    {
        if (previous.Blocks.Count == 0
            || offset != previous.SourceLength
            || sourceLength <= 0)
        {
            return null;
        }

        FileInfo sourceInfo;
        try
        {
            sourceInfo = new FileInfo(sourcePath);
            if (!IsEligibleSource(sourceInfo)
                || sourceInfo.Length != checked(previous.SourceLength + sourceLength))
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }

        var last = previous.Blocks[^1];
        if (last.Offset + last.Length != previous.SourceLength)
        {
            return null;
        }

        var blocks = previous.Blocks.ToList();
        if (last.RecordCount < ConfigurationLimits.HistorySearchIndexBlockRecords)
        {
            var updatedLength = checked(last.Length + sourceLength);
            blocks[^1] = AppendToBlock(last, record, sourceLength, HashSourceRange(sourcePath, last.Offset, updatedLength));
        }
        else
        {
            if (blocks.Count >= ConfigurationLimits.MaximumHistorySearchIndexBlocks)
            {
                return null;
            }

            var builder = new BlockBuilder();
            builder.Add(offset, record);
            blocks.Add(builder.Complete(sourceLength, HashSourceRange(sourcePath, offset, sourceLength)));
        }

        var snapshot = new JsonlSearchIndexSnapshot(
            sourceInfo.Length,
            sourceInfo.LastWriteTimeUtc.Ticks,
            blocks);
        try
        {
            await WriteAsync(GetSidecarPath(sourcePath), snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return snapshot;
        }

        try
        {
            var sidecarInfo = new FileInfo(GetSidecarPath(sourcePath));
            return sidecarInfo.Exists
                ? snapshot with { SidecarLength = sidecarInfo.Length, SidecarLastWriteTicks = sidecarInfo.LastWriteTimeUtc.Ticks }
                : snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return snapshot;
        }
    }

    public static bool IsSidecarCurrent(string path, JsonlSearchIndexSnapshot snapshot)
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

    public static bool MayContain(JsonlSearchIndexBlock block, string query)
    {
        var normalized = Normalize(query);
        if (normalized is null || normalized.Length < 3)
        {
            return true;
        }

        for (var index = 0; index <= normalized.Length - 3; index++)
        {
            if (!ContainsTrigram(block.Bloom, normalized, index))
            {
                return false;
            }
        }

        return true;
    }

    public static bool CanUseTextFilter(string query) => Normalize(query) is { Length: >= 3 };

    private static bool TryRead(
        string path,
        string sourcePath,
        long sourceLength,
        long sourceLastWriteTicks,
        out JsonlSearchIndexSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            var payloadLength = stream.Length - SidecarHashBytes;
            var maximumSidecarLength = HeaderBytes
                + (long)ConfigurationLimits.MaximumHistorySearchIndexBlocks * BlockBytes
                + SidecarHashBytes;
            if (payloadLength < HeaderBytes || stream.Length > maximumSidecarLength)
            {
                return false;
            }

            var payload = new byte[checked((int)payloadLength)];
            stream.ReadExactly(payload);
            var storedHash = new byte[SidecarHashBytes];
            stream.ReadExactly(storedHash);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), storedHash))
            {
                return false;
            }

            var header = payload.AsSpan(0, HeaderBytes);
            if (!header[..Magic.Length].SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header[Magic.Length..]) != FormatVersion)
            {
                return false;
            }

            var indexedLength = BinaryPrimitives.ReadInt64LittleEndian(header[12..]);
            var indexedLastWriteTicks = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
            var blockSize = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
            var bloomBytes = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
            if (indexedLength != sourceLength
                || indexedLastWriteTicks != sourceLastWriteTicks
                || blockSize != ConfigurationLimits.HistorySearchIndexBlockRecords
                || bloomBytes != ConfigurationLimits.HistorySearchIndexBloomBytes
                || count < 0
                || count > ConfigurationLimits.MaximumHistorySearchIndexBlocks
                || payloadLength != HeaderBytes + (long)count * BlockBytes)
            {
                return false;
            }

            var blocks = new List<JsonlSearchIndexBlock>(count);
            var blockBuffer = new byte[BlockBytes];
            long previousOffset = -1;
            long previousEnd = 0;
            for (var index = 0; index < count; index++)
            {
                payload.AsSpan(HeaderBytes + index * BlockBytes, BlockBytes).CopyTo(blockBuffer);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(blockBuffer);
                var length = BinaryPrimitives.ReadInt64LittleEndian(blockBuffer.AsSpan(8));
                var minimum = BinaryPrimitives.ReadInt64LittleEndian(blockBuffer.AsSpan(16));
                var maximum = BinaryPrimitives.ReadInt64LittleEndian(blockBuffer.AsSpan(24));
                var recordCount = BinaryPrimitives.ReadInt32LittleEndian(blockBuffer.AsSpan(32));
                if (offset <= previousOffset
                    || offset < 0
                    || offset < previousEnd
                    || length <= 0
                    || length > sourceLength - offset
                    || recordCount <= 0
                    || recordCount > ConfigurationLimits.HistorySearchIndexBlockRecords
                    || minimum > maximum)
                {
                    return false;
                }

                var sourceFingerprint = blockBuffer.AsSpan(36, SourceFingerprintBytes).ToArray();
                var bloom = blockBuffer.AsSpan(36 + SourceFingerprintBytes, ConfigurationLimits.HistorySearchIndexBloomBytes).ToArray();
                blocks.Add(new JsonlSearchIndexBlock(offset, length, minimum, maximum, recordCount, bloom, sourceFingerprint));
                previousOffset = offset;
                previousEnd = offset + length;
            }

            if (blocks.Count > 0 && blocks[^1].Offset + blocks[^1].Length != sourceLength)
            {
                return false;
            }

            if (!ValidateSourceFingerprints(sourcePath, sourceLength, sourceLastWriteTicks, blocks))
            {
                return false;
            }

            var sidecarInfo = new FileInfo(path);
            snapshot = new JsonlSearchIndexSnapshot(sourceLength, sourceLastWriteTicks, blocks, sidecarInfo.Length, sidecarInfo.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static async Task WriteAsync(string path, JsonlSearchIndexSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var payload = new byte[checked(HeaderBytes + snapshot.Blocks.Count * BlockBytes)];
            var header = payload.AsSpan(0, HeaderBytes);
            Magic.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], FormatVersion);
            BinaryPrimitives.WriteInt64LittleEndian(header[12..], snapshot.SourceLength);
            BinaryPrimitives.WriteInt64LittleEndian(header[20..], snapshot.SourceLastWriteTicks);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..], ConfigurationLimits.HistorySearchIndexBlockRecords);
            BinaryPrimitives.WriteInt32LittleEndian(header[32..], ConfigurationLimits.HistorySearchIndexBloomBytes);
            BinaryPrimitives.WriteInt32LittleEndian(header[36..], snapshot.Blocks.Count);

            var blockBuffer = new byte[BlockBytes];
            for (var index = 0; index < snapshot.Blocks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = snapshot.Blocks[index];
                BinaryPrimitives.WriteInt64LittleEndian(blockBuffer, block.Offset);
                BinaryPrimitives.WriteInt64LittleEndian(blockBuffer.AsSpan(8), block.Length);
                BinaryPrimitives.WriteInt64LittleEndian(blockBuffer.AsSpan(16), block.MinimumTimestampTicks);
                BinaryPrimitives.WriteInt64LittleEndian(blockBuffer.AsSpan(24), block.MaximumTimestampTicks);
                BinaryPrimitives.WriteInt32LittleEndian(blockBuffer.AsSpan(32), block.RecordCount);
                if (block.SourceFingerprint.Length != SourceFingerprintBytes || block.Bloom.Length != ConfigurationLimits.HistorySearchIndexBloomBytes)
                {
                    throw new InvalidDataException("The search index block fingerprint or Bloom filter has an invalid size.");
                }

                block.SourceFingerprint.CopyTo(blockBuffer.AsSpan(36, SourceFingerprintBytes));
                block.Bloom.CopyTo(blockBuffer.AsSpan(36 + SourceFingerprintBytes, ConfigurationLimits.HistorySearchIndexBloomBytes));
                blockBuffer.CopyTo(payload, HeaderBytes + index * BlockBytes);
            }

            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(SHA256.HashData(payload), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A disposable sidecar cleanup failure cannot affect JSONL.
            }
        }
    }

    private static JsonlSearchIndexBlock[] AddSourceFingerprints(
        string sourcePath,
        long sourceLength,
        long sourceLastWriteTicks,
        IReadOnlyList<JsonlSearchIndexBlock> blocks)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (source.Length != sourceLength)
        {
            throw new IOException("The canonical history changed while its search index was being built.");
        }

        var fingerprinted = blocks
            .Select(block => block with { SourceFingerprint = HashSourceRange(source, block.Offset, block.Length) })
            .ToArray();
        var after = new FileInfo(sourcePath);
        if (!IsSameSource(after, sourceLength, sourceLastWriteTicks))
        {
            throw new IOException("The canonical history changed while its search index was being built.");
        }

        return fingerprinted;
    }

    private static bool ValidateSourceFingerprints(
        string sourcePath,
        long sourceLength,
        long sourceLastWriteTicks,
        IReadOnlyList<JsonlSearchIndexBlock> blocks)
    {
        try
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            if (source.Length != sourceLength)
            {
                return false;
            }

            foreach (var block in blocks)
            {
                if (block.SourceFingerprint.Length != SourceFingerprintBytes
                    || !CryptographicOperations.FixedTimeEquals(
                        block.SourceFingerprint,
                        HashSourceRange(source, block.Offset, block.Length)))
                {
                    return false;
                }
            }

            var after = new FileInfo(sourcePath);
            return IsSameSource(after, sourceLength, sourceLastWriteTicks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }

    private static byte[] HashSourceRange(FileStream source, long offset, long length)
    {
        if (offset < 0 || length <= 0 || offset > source.Length || length > source.Length - offset)
        {
            throw new EndOfStreamException("The search index block is outside the canonical history source.");
        }

        source.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new EndOfStreamException("The canonical history ended while hashing a search index block.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return hash.GetHashAndReset();
    }

    private static byte[] HashSourceRange(string sourcePath, long offset, long length)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        return HashSourceRange(source, offset, length);
    }

    private static bool IsEligibleSource(FileInfo sourceInfo) => sourceInfo.Exists
        && sourceInfo.Length >= ConfigurationLimits.MinimumHistoryIndexFileBytes
        && sourceInfo.Length <= ConfigurationLimits.MaximumHistoryFileBytes;

    private static bool IsSameSource(FileInfo sourceInfo, long length, long lastWriteTicks) => sourceInfo.Exists
        && sourceInfo.Length == length
        && sourceInfo.LastWriteTimeUtc.Ticks == lastWriteTicks;

    private static JsonlSearchIndexBlock AppendToBlock(
        JsonlSearchIndexBlock block,
        ConversationLogRecord record,
        int sourceLength,
        byte[] sourceFingerprint)
    {
        var bloom = (byte[])block.Bloom.Clone();
        AddText(bloom, record.Text);
        if (record.Sender is not null) AddText(bloom, record.Sender);
        AddText(bloom, record.ConversationName);
        return new JsonlSearchIndexBlock(
            block.Offset,
            checked(block.Length + sourceLength),
            Math.Min(block.MinimumTimestampTicks, record.Timestamp.UtcTicks),
            Math.Max(block.MaximumTimestampTicks, record.Timestamp.UtcTicks),
            checked(block.RecordCount + 1),
            bloom,
            sourceFingerprint);
    }

    private static void AddText(byte[] bloom, string value)
    {
        var normalized = Normalize(value);
        if (normalized is null) return;
        for (var index = 0; index <= normalized.Length - 3; index++) AddTrigram(bloom, normalized, index);
    }

    private static string? Normalize(string value)
    {
        if (value.Any(character => character > 127))
        {
            return null;
        }

        return value.ToUpperInvariant();
    }

    private static bool ContainsTrigram(byte[] bloom, string value, int index)
    {
        var (first, second) = Hash(value, index);
        for (var hash = 0; hash < HashCount; hash++)
        {
            var bit = (int)((first + (uint)hash * second) % (uint)BloomBits);
            if ((bloom[bit / 8] & (1 << (bit % 8))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddTrigram(byte[] bloom, string value, int index)
    {
        var (first, second) = Hash(value, index);
        for (var hash = 0; hash < HashCount; hash++)
        {
            var bit = (int)((first + (uint)hash * second) % (uint)BloomBits);
            bloom[bit / 8] |= (byte)(1 << (bit % 8));
        }
    }

    private static (uint First, uint Second) Hash(string value, int index)
    {
        uint first = 2166136261;
        uint second = 16777619;
        for (var offset = 0; offset < 3; offset++)
        {
            first = (first ^ value[index + offset]) * 16777619;
            second = (second ^ value[index + offset]) * 2246822519;
        }

        return (first, second | 1);
    }

    internal sealed class BlockBuilder
    {
        private readonly byte[] _bloom = new byte[ConfigurationLimits.HistorySearchIndexBloomBytes];
        private long _minimumTimestampTicks = long.MaxValue;
        private long _maximumTimestampTicks = long.MinValue;

        public long Offset { get; private set; }

        public int RecordCount { get; private set; }

        public void Add(long offset, ConversationLogRecord record)
        {
            if (RecordCount == 0)
            {
                Offset = offset;
            }

            RecordCount++;
            _minimumTimestampTicks = Math.Min(_minimumTimestampTicks, record.Timestamp.UtcTicks);
            _maximumTimestampTicks = Math.Max(_maximumTimestampTicks, record.Timestamp.UtcTicks);
            AddText(_bloom, record.Text);
            if (record.Sender is not null) AddText(_bloom, record.Sender);
            AddText(_bloom, record.ConversationName);
        }

        public JsonlSearchIndexBlock Complete(long length, byte[]? sourceFingerprint = null) => new(
            Offset,
            length,
            _minimumTimestampTicks,
            _maximumTimestampTicks,
            RecordCount,
            _bloom,
            sourceFingerprint ?? Array.Empty<byte>());

        private static void AddText(byte[] bloom, string value)
        {
            var normalized = Normalize(value);
            if (normalized is null) return;
            for (var index = 0; index <= normalized.Length - 3; index++) AddTrigram(bloom, normalized, index);
        }
    }
}

internal sealed class JsonlSearchIndexBuilder
{
    private readonly List<JsonlSearchIndexBlock> _blocks = [];
    private JsonlSearchIndex.BlockBuilder _current = new();
    private bool _tooManyBlocks;

    public void Add(long offset, ConversationLogRecord record)
    {
        if (_tooManyBlocks)
        {
            return;
        }

        if (_current.RecordCount == ConfigurationLimits.HistorySearchIndexBlockRecords)
        {
            _blocks.Add(_current.Complete(offset - _current.Offset));
            if (_blocks.Count >= ConfigurationLimits.MaximumHistorySearchIndexBlocks)
            {
                _tooManyBlocks = true;
                return;
            }

            _current = new JsonlSearchIndex.BlockBuilder();
        }

        _current.Add(offset, record);
    }

    public IReadOnlyList<JsonlSearchIndexBlock>? Complete(long sourceLength)
    {
        if (_tooManyBlocks)
        {
            return null;
        }

        if (_current.RecordCount > 0)
        {
            _blocks.Add(_current.Complete(sourceLength - _current.Offset));
        }

        return _blocks;
    }
}
