using System.Buffers;
using System.Text;

namespace nexIRC.Core.Protocol;

public sealed class IrcLineTooLongException : Exception
{
    public IrcLineTooLongException(int maximumLineBytes)
        : base($"The IRC line exceeded the configured maximum of {maximumLineBytes} bytes.")
    {
        MaximumLineBytes = maximumLineBytes;
    }

    public int MaximumLineBytes { get; }
}

public sealed class IrcLineFrame
{
    public IrcLineFrame(ReadOnlySpan<byte> bytes)
    {
        var copy = bytes.ToArray();
        Bytes = copy;
        Text = Encoding.UTF8.GetString(copy);
    }

    public ReadOnlyMemory<byte> Bytes { get; }

    public string Text { get; }
}

public sealed class IrcFramerDisconnectResult
{
    internal IrcFramerDisconnectResult(ReadOnlySpan<byte> incompleteBytes)
    {
        if (incompleteBytes.IsEmpty)
        {
            IncompleteBytes = ReadOnlyMemory<byte>.Empty;
            IncompleteText = null;
        }
        else
        {
            var copy = incompleteBytes.ToArray();
            IncompleteBytes = copy;
            IncompleteText = Encoding.UTF8.GetString(copy);
        }
    }

    public bool HasIncompleteLine => !IncompleteBytes.IsEmpty;

    public ReadOnlyMemory<byte> IncompleteBytes { get; }

    public string? IncompleteText { get; }
}

/// <summary>
/// Incrementally turns arbitrary byte chunks into IRC lines delimited by CRLF.
/// The limit applies to the line bytes before CRLF and is deliberately bounded.
/// </summary>
public sealed class IrcLineFramer
{
    public const int DefaultMaximumLineBytes = 8192;
    public const int MaximumSupportedLineBytes = 16 * 1024 * 1024;

    private readonly byte[] _lineBuffer;
    private readonly int _maximumLineBytes;
    private int _length;
    private bool _pendingCarriageReturn;

    public IrcLineFramer(int maximumLineBytes = DefaultMaximumLineBytes)
    {
        if (maximumLineBytes is < 1 or > MaximumSupportedLineBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLineBytes), maximumLineBytes, $"The line limit must be between 1 and {MaximumSupportedLineBytes} bytes.");
        }

        _maximumLineBytes = maximumLineBytes;
        // One extra byte is reserved for a trailing CR observed immediately before disconnect.
        _lineBuffer = new byte[maximumLineBytes + 1];
    }

    public int MaximumLineBytes => _maximumLineBytes;

    public IReadOnlyList<IrcLineFrame> Push(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return Array.Empty<IrcLineFrame>();
        }

        var frames = new List<IrcLineFrame>();
        foreach (var value in chunk)
        {
            if (_pendingCarriageReturn)
            {
                if (value == (byte)'\n')
                {
                    frames.Add(new IrcLineFrame(_lineBuffer.AsSpan(0, _length)));
                    _length = 0;
                    _pendingCarriageReturn = false;
                    continue;
                }

                Append((byte)'\r');
                _pendingCarriageReturn = false;
            }

            if (value == (byte)'\r')
            {
                _pendingCarriageReturn = true;
            }
            else
            {
                Append(value);
            }
        }

        return frames;
    }

    public IrcFramerDisconnectResult Disconnect()
    {
        var length = _length + (_pendingCarriageReturn ? 1 : 0);
        if (length == 0)
        {
            Reset();
            return new IrcFramerDisconnectResult(ReadOnlySpan<byte>.Empty);
        }

        if (_pendingCarriageReturn)
        {
            _lineBuffer[_length] = (byte)'\r';
        }

        var result = new IrcFramerDisconnectResult(_lineBuffer.AsSpan(0, length));
        Reset();
        return result;
    }

    public void Reset()
    {
        _length = 0;
        _pendingCarriageReturn = false;
    }

    private void Append(byte value)
    {
        if (_length >= _maximumLineBytes)
        {
            Reset();
            throw new IrcLineTooLongException(_maximumLineBytes);
        }

        _lineBuffer[_length++] = value;
    }
}
