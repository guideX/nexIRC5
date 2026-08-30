namespace nexIRC.Application;

public enum InputHistoryDirection
{
    Older,
    Newer
}

/// <summary>
/// Bounded, presentation-neutral history for submitted IRC input.
/// Navigation returns strings, so editing a recalled value never edits the
/// stored entry.
/// </summary>
public sealed class InputHistory
{
    public const int DefaultMaximumEntries = 100;

    private readonly object _gate = new();
    private readonly List<string> _entries = [];
    private readonly int _maximumEntries;
    private int _position = -1;
    private string _draft = string.Empty;

    public InputHistory(int maximumEntries = DefaultMaximumEntries)
    {
        if (maximumEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _maximumEntries = maximumEntries;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public IReadOnlyList<string> EntriesSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Submit(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = input.Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.Count == 0 || !string.Equals(_entries[^1], normalized, StringComparison.Ordinal))
            {
                _entries.Add(normalized);
                while (_entries.Count > _maximumEntries)
                {
                    _entries.RemoveAt(0);
                }
            }

            ResetNavigationUnsafe();
        }
    }

    public string Navigate(InputHistoryDirection direction, string currentInput)
    {
        ArgumentNullException.ThrowIfNull(currentInput);
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return currentInput;
            }

            if (_position == -1)
            {
                _draft = currentInput;
            }

            if (direction == InputHistoryDirection.Older)
            {
                _position = _position == -1 ? _entries.Count - 1 : Math.Max(0, _position - 1);
                return _entries[_position];
            }

            if (_position == -1)
            {
                return currentInput;
            }

            if (_position < _entries.Count - 1)
            {
                _position++;
                return _entries[_position];
            }

            var draft = _draft;
            ResetNavigationUnsafe();
            return draft;
        }
    }

    public void ResetNavigation()
    {
        lock (_gate)
        {
            ResetNavigationUnsafe();
        }
    }

    private void ResetNavigationUnsafe()
    {
        _position = -1;
        _draft = string.Empty;
    }
}
