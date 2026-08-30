using nexIRC.Core.State;

namespace nexIRC.Application;

public sealed record CompletionResult(string Text, int CaretIndex, bool Completed, string? Candidate = null)
{
    public static CompletionResult Unchanged(string text, int caretIndex) => new(text, caretIndex, false);
}

/// <summary>
/// Context-sensitive IRC command and channel-member completion. The engine
/// keeps only a small cycling cursor; candidates always come from the current
/// dispatcher registry or the current network/channel projection.
/// </summary>
public sealed class CompletionEngine
{
    private CompletionCycle? _cycle;

    public CompletionResult Complete(
        string input,
        int caretIndex,
        NetworkWorkspace? network,
        WorkspaceView? activeView)
    {
        ArgumentNullException.ThrowIfNull(input);
        caretIndex = Math.Clamp(caretIndex, 0, input.Length);
        if (_cycle is { } previousCycle
            && string.Equals(previousCycle.LastText, input, StringComparison.Ordinal)
            && previousCycle.LastCaretIndex == caretIndex)
        {
            var nextIndex = (previousCycle.Index + 1) % previousCycle.Candidates.Length;
            var candidate = previousCycle.Candidates[nextIndex];
            var suffix = previousCycle.Key.Type == "nickname" && previousCycle.Key.TokenStart == 0 ? ": " : string.Empty;
            var replacement = candidate + suffix;
            var cycledText = input[..previousCycle.Key.TokenStart] + replacement + input[(previousCycle.Key.TokenStart + previousCycle.ReplacementLength)..];
            var cycledResult = new CompletionResult(cycledText, previousCycle.Key.TokenStart + replacement.Length, true, candidate);
            _cycle = previousCycle with
            {
                Index = nextIndex,
                LastText = cycledText,
                LastCaretIndex = cycledResult.CaretIndex,
                ReplacementLength = replacement.Length
            };
            return cycledResult;
        }

        var tokenStart = FindTokenStart(input, caretIndex);
        var tokenEnd = FindTokenEnd(input, caretIndex);
        var token = input[tokenStart..caretIndex];

        if (input.Length > 0 && tokenStart == 0 && input[0] == '/')
        {
            var commandPrefix = token[1..];
            var commands = IrcCommandDispatcher.SupportedCommands
                .Where(command => command.StartsWith(commandPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(static command => "/" + command.ToLowerInvariant())
                .ToArray();
            return Replace(input, tokenStart, tokenEnd, commands, caretIndex, "command", network, activeView, token);
        }

        if (network is null || activeView is not ChannelView channel || channel.NetworkId != network.Id || token.Length == 0)
        {
            _cycle = null;
            return CompletionResult.Unchanged(input, caretIndex);
        }

        var mapping = network.Snapshot.Features.CaseMapping;
        var foldedToken = IrcCaseMappingComparer.Fold(token, mapping);
        var nicknames = channel.MembersSnapshot
            .Select(static member => member.Nickname)
            .Where(nickname => IrcCaseMappingComparer.Fold(nickname, mapping).StartsWith(foldedToken, StringComparison.Ordinal))
            .Distinct(IrcCaseMappingComparer.For(mapping))
            .OrderBy(nickname => IrcCaseMappingComparer.Fold(nickname, mapping), StringComparer.Ordinal)
            .ThenBy(static nickname => nickname, StringComparer.Ordinal)
            .ToArray();
        return Replace(input, tokenStart, tokenEnd, nicknames, caretIndex, "nickname", network, activeView, token);
    }

    private CompletionResult Replace(
        string input,
        int tokenStart,
        int tokenEnd,
        string[] candidates,
        int caretIndex,
        string cycleType,
        NetworkWorkspace? network,
        WorkspaceView? activeView,
        string token)
    {
        if (candidates.Length == 0)
        {
            _cycle = null;
            return CompletionResult.Unchanged(input, caretIndex);
        }

        var key = new CompletionKey(
            cycleType,
            network?.Id,
            activeView?.Id,
            tokenStart,
            token);
        var index = _cycle is { Key: var previousKey } && previousKey.Equals(key)
            ? (_cycle.Index + 1) % candidates.Length
            : 0;
        var candidate = candidates[index];
        var suffix = cycleType == "nickname" && tokenStart == 0 ? ": " : string.Empty;
        var replacement = candidate + suffix;
        var resultText = input[..tokenStart] + replacement + input[tokenEnd..];
        var result = new CompletionResult(resultText, tokenStart + replacement.Length, true, candidate);
        _cycle = new CompletionCycle(key, index, candidates, result.Text, result.CaretIndex, replacement.Length);
        return result;
    }

    private static int FindTokenStart(string input, int caretIndex)
    {
        var index = caretIndex;
        while (index > 0 && !char.IsWhiteSpace(input[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static int FindTokenEnd(string input, int caretIndex)
    {
        var index = caretIndex;
        while (index < input.Length && !char.IsWhiteSpace(input[index]))
        {
            index++;
        }

        return index;
    }

    private sealed record CompletionCycle(
        CompletionKey Key,
        int Index,
        string[] Candidates,
        string LastText,
        int LastCaretIndex,
        int ReplacementLength);

    private sealed record CompletionKey(
        string Type,
        Guid? NetworkId,
        Guid? ViewId,
        int TokenStart,
        string Token);
}
