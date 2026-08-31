namespace nexIRC.Application;

public readonly record struct ViewportBounds(double Left, double Top, double Right, double Bottom)
{
    public static ViewportBounds Default => new(0, 0, 1920, 1080);
}

public static class ViewStateValidator
{
    public static ViewStatePreferences Normalize(ViewStatePreferences? state, ViewportBounds? bounds = null)
    {
        state ??= new ViewStatePreferences();
        var viewport = bounds ?? ViewportBounds.Default;
        var width = IsFinite(state.WindowWidth) ? Math.Clamp(state.WindowWidth, 640, 8000) : 1180;
        var height = IsFinite(state.WindowHeight) ? Math.Clamp(state.WindowHeight, 420, 5000) : 760;
        var left = state.WindowLeft is double leftValue && IsFinite(leftValue) ? leftValue : (double?)null;
        var top = state.WindowTop is double topValue && IsFinite(topValue) ? topValue : (double?)null;
        if (left is double leftCoordinate && top is double topCoordinate && (leftCoordinate + width < viewport.Left + 40 || leftCoordinate > viewport.Right - 40 || topCoordinate + height < viewport.Top + 40 || topCoordinate > viewport.Bottom - 40))
        {
            left = null;
            top = null;
        }

        return state with
        {
            WindowWidth = width,
            WindowHeight = height,
            WindowLeft = left,
            WindowTop = top,
            NavigationPaneWidth = IsFinite(state.NavigationPaneWidth) ? Math.Clamp(state.NavigationPaneWidth, 160, 600) : 260,
            MemberPaneWidth = IsFinite(state.MemberPaneWidth) ? Math.Clamp(state.MemberPaneWidth, 140, 600) : 220,
            LastLogSearchQuery = NormalizeText(state.LastLogSearchQuery, ConfigurationLimits.MaximumSearchQueryLength),
            LastLogConversation = NormalizeText(state.LastLogConversation, ConfigurationLimits.MaximumChannelLength)
        };
    }

    private static string? NormalizeText(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Any(character => character is '\r' or '\n' or '\0')
            ? null
            : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
