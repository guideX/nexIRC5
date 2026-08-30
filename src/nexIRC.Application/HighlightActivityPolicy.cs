using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

/// <summary>
/// In-memory policy for translating semantic IRC activity into workspace
/// activity. It deliberately knows nothing about transcript rendering or WPF.
/// </summary>
public sealed class HighlightActivityPolicy : ObservableObject
{
    private bool _highlightNickname = true;
    private bool _highlightCustomWords = true;
    private IReadOnlyList<string> _customWords = Array.Empty<string>();

    public bool HighlightNickname
    {
        get => _highlightNickname;
        set => SetProperty(ref _highlightNickname, value);
    }

    public bool HighlightCustomWords
    {
        get => _highlightCustomWords;
        set => SetProperty(ref _highlightCustomWords, value);
    }

    public IReadOnlyList<string> CustomWords
    {
        get => _customWords;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var words = value
                .Where(static word => !string.IsNullOrWhiteSpace(word))
                .Select(static word => word.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SetProperty(ref _customWords, words);
        }
    }

    public WorkspaceActivity Classify(WorkspaceView view, IrcSemanticEvent semanticEvent, ServerSessionSnapshot snapshot)
    {
        if (view.IsActive)
        {
            return WorkspaceActivity.None;
        }

        if (semanticEvent is IrcQueryMessageEvent)
        {
            return WorkspaceActivity.Important;
        }

        if (semanticEvent is IrcPrivmsgEvent message)
        {
            if (message.IsNotice || view is QueryView)
            {
                return WorkspaceActivity.Important;
            }

            if (view is ChannelView && IsHighlight(message.Text, snapshot.Nickname, snapshot.Features.CaseMapping))
            {
                return WorkspaceActivity.Important;
            }

            return WorkspaceActivity.Unread;
        }

        if (semanticEvent is IrcServerErrorEvent || semanticEvent is IrcUnknownCommandEvent || semanticEvent is IrcUnknownNumericEvent)
        {
            return WorkspaceActivity.Important;
        }

        return WorkspaceActivity.Unread;
    }

    public bool IsHighlight(string text, string nickname, IrcCaseMapping mapping)
    {
        if (HighlightNickname && ContainsWord(text, nickname, mapping))
        {
            return true;
        }

        return HighlightCustomWords && CustomWords.Any(word => ContainsWord(text, word, mapping));
    }

    public static bool ContainsWord(string text, string word, IrcCaseMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        var foldedText = IrcCaseMappingComparer.Fold(text, mapping);
        var foldedWord = IrcCaseMappingComparer.Fold(word, mapping);
        var start = 0;
        while ((start = foldedText.IndexOf(foldedWord, start, StringComparison.Ordinal)) >= 0)
        {
            var beforeIsWord = start > 0 && IsNicknameCharacter(foldedText[start - 1]);
            var end = start + foldedWord.Length;
            var afterIsWord = end < foldedText.Length && IsNicknameCharacter(foldedText[end]);
            if (!beforeIsWord && !afterIsWord)
            {
                return true;
            }

            start = end;
        }

        return false;
    }

    private static bool IsNicknameCharacter(char character) => char.IsLetterOrDigit(character)
        || character is '-' or '_' or '[' or ']' or '\\' or '\u0060' or '^' or '{' or '}' or '|';
}
