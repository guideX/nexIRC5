using System.Text;
using System.Text.RegularExpressions;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum DestinationKind
{
    Channel,
    Query
}

public sealed record FavoriteDestination
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable profile ID when saved, or the runtime network ID for a temporary connection.</summary>
    public Guid ScopeId { get; init; }

    public DestinationKind Kind { get; init; } = DestinationKind.Channel;

    public string Name { get; init; } = string.Empty;

    public string? Label { get; init; }
}

public sealed record RecentDestination
{
    public Guid ScopeId { get; init; }

    public DestinationKind Kind { get; init; } = DestinationKind.Channel;

    public string Name { get; init; } = string.Empty;

    public DateTimeOffset LastOpened { get; init; }
}

public static class NavigationValidator
{
    public static FavoriteDestination? NormalizeFavorite(FavoriteDestination? favorite)
    {
        if (favorite is null || favorite.ScopeId == Guid.Empty)
        {
            return null;
        }

        var name = favorite.Name.Trim();
        if (name.Length == 0 || name.Length > ConfigurationLimits.MaximumChannelLength || name.Any(char.IsWhiteSpace) || name.Any(IsLineBreak))
        {
            return null;
        }

        return favorite with
        {
            Id = favorite.Id == Guid.Empty ? Guid.NewGuid() : favorite.Id,
            Name = name,
            Label = NormalizeLabel(favorite.Label)
        };
    }

    public static RecentDestination? NormalizeRecent(RecentDestination? recent)
    {
        if (recent is null || recent.ScopeId == Guid.Empty)
        {
            return null;
        }

        var name = recent.Name.Trim();
        if (name.Length == 0 || name.Length > ConfigurationLimits.MaximumChannelLength || name.Any(char.IsWhiteSpace) || name.Any(IsLineBreak))
        {
            return null;
        }

        return recent with
        {
            Name = name,
            LastOpened = recent.LastOpened == default ? DateTimeOffset.UtcNow : recent.LastOpened
        };
    }

    public static bool SameDestination(FavoriteDestination left, FavoriteDestination right) =>
        left.ScopeId == right.ScopeId && left.Kind == right.Kind && IrcIdentity.Equals(left.Name, right.Name, IrcCaseMapping.Rfc1459);

    public static bool SameDestination(RecentDestination left, RecentDestination right) =>
        left.ScopeId == right.ScopeId && left.Kind == right.Kind && IrcIdentity.Equals(left.Name, right.Name, IrcCaseMapping.Rfc1459);

    public static List<FavoriteDestination> NormalizeFavorites(IEnumerable<FavoriteDestination>? favorites)
    {
        var result = new List<FavoriteDestination>();
        foreach (var favorite in (favorites ?? []).Take(ConfigurationLimits.MaximumProfiles * ConfigurationLimits.MaximumFavoritesPerProfile))
        {
            var normalized = NormalizeFavorite(favorite);
            if (normalized is null || result.Count(item => item.ScopeId == normalized.ScopeId) >= ConfigurationLimits.MaximumFavoritesPerProfile || result.Any(existing => SameDestination(existing, normalized)))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    public static List<RecentDestination> BoundRecents(IEnumerable<RecentDestination> recents)
    {
        var result = new List<RecentDestination>();
        foreach (var recent in recents.OrderByDescending(item => item.LastOpened))
        {
            if (result.Any(existing => SameDestination(existing, recent)))
            {
                continue;
            }

            var maximum = recent.Kind == DestinationKind.Channel
                ? ConfigurationLimits.MaximumRecentChannelsPerProfile
                : ConfigurationLimits.MaximumRecentQueriesPerProfile;
            if (result.Count(item => item.ScopeId == recent.ScopeId && item.Kind == recent.Kind) >= maximum)
            {
                continue;
            }

            result.Add(recent);
        }

        return result;
    }

    private static string? NormalizeLabel(string? label)
    {
        var normalized = label?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized[..Math.Min(normalized.Length, ConfigurationLimits.MaximumStringLength)];
    }

    private static bool IsLineBreak(char value) => value is '\r' or '\n';
}

public sealed record AliasDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string Expansion { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;

    public string? Description { get; init; }
}

public sealed record AliasValidationResult(bool IsValid, string? Error = null);

public static class AliasValidator
{
    public static AliasDefinition? Normalize(AliasDefinition? alias)
    {
        if (alias is null)
        {
            return null;
        }

        var name = alias.Name.Trim().TrimStart('/');
        var expansion = alias.Expansion.Trim();
        if (name.Length == 0 || name.Length > ConfigurationLimits.MaximumAliasNameLength || expansion.Length == 0 || expansion.Length > ConfigurationLimits.MaximumAliasExpansionLength)
        {
            return null;
        }

        if (!name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-'))
        {
            return null;
        }

        if (name.Any(char.IsWhiteSpace) || expansion.Any(character => character is '\r' or '\n' or '\0'))
        {
            return null;
        }

        var description = alias.Description?.Trim();
        return alias with
        {
            Id = alias.Id == Guid.Empty ? Guid.NewGuid() : alias.Id,
            Name = name.ToLowerInvariant(),
            Expansion = expansion,
            Description = string.IsNullOrWhiteSpace(description) ? null : description[..Math.Min(description.Length, ConfigurationLimits.MaximumStringLength)]
        };
    }

    public static AliasValidationResult Validate(AliasDefinition? alias)
    {
        if (Normalize(alias) is null)
        {
            return new AliasValidationResult(false, "Alias names must be short command tokens and expansions must be non-empty, line-free, and bounded.");
        }

        if (alias is not null && IrcCommandDispatcher.IsBuiltInCommand(alias.Name.Trim().TrimStart('/')))
        {
            return new AliasValidationResult(false, "Built-in commands are authoritative; choose another alias name.");
        }

        return new AliasValidationResult(true);
    }
}

public sealed record AliasExpansionResult(bool Succeeded, string Input, string? Error = null, int Depth = 0)
{
    public static AliasExpansionResult NoExpansion(string input) => new(true, input);
}

public static partial class AliasExpander
{
    [GeneratedRegex(@"\$(\*|[1-9][0-9]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentPattern();

    public static AliasExpansionResult Expand(string input, IReadOnlyList<AliasDefinition> aliases)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(aliases);
        var current = input.Trim();
        if (current.Length == 0 || current[0] != '/')
        {
            return AliasExpansionResult.NoExpansion(input);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 1; depth <= ConfigurationLimits.MaximumAliasRecursionDepth; depth++)
        {
            var parsed = Parse(current);
            if (parsed is null)
            {
                return AliasExpansionResult.NoExpansion(current);
            }

            var alias = aliases.FirstOrDefault(candidate => candidate.IsEnabled && string.Equals(candidate.Name, parsed.Value.Name, StringComparison.OrdinalIgnoreCase));
            if (alias is null)
            {
                return new AliasExpansionResult(true, current, Depth: depth - 1);
            }

            if (!visited.Add(alias.Name))
            {
                return new AliasExpansionResult(false, current, $"Alias loop detected at /{alias.Name}.", depth);
            }

            var expansion = Substitute(alias.Expansion, parsed.Value.Arguments);
            if (expansion.Length > ConfigurationLimits.MaximumAliasExpansionLength)
            {
                return new AliasExpansionResult(false, current, "Alias expansion exceeds the supported length.", depth);
            }

            current = expansion[0] == '/' ? expansion : "/" + expansion;
        }

        return new AliasExpansionResult(false, current, $"Alias expansion exceeded the maximum depth of {ConfigurationLimits.MaximumAliasRecursionDepth}.", ConfigurationLimits.MaximumAliasRecursionDepth);
    }

    private static string Substitute(string expansion, IReadOnlyList<string> arguments) => ArgumentPattern().Replace(expansion, match =>
    {
        if (match.Groups[1].Value == "*")
        {
            return string.Join(' ', arguments);
        }

        return int.TryParse(match.Groups[1].Value, out var index) && index <= arguments.Count ? arguments[index - 1] : string.Empty;
    });

    private static (string Name, IReadOnlyList<string> Arguments)? Parse(string input)
    {
        var line = input[1..].TrimStart();
        if (line.Length == 0)
        {
            return null;
        }

        var tokens = Tokenize(line);
        return tokens.Count == 0 ? null : (tokens[0], tokens.Skip(1).ToArray());
    }

    private static List<string> Tokenize(string value)
    {
        var result = new List<string>();
        var buffer = new StringBuilder();
        var quote = '\0';
        foreach (var character in value)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    buffer.Append(character);
                }
            }
            else if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (buffer.Length > 0)
                {
                    result.Add(buffer.ToString());
                    buffer.Clear();
                }
            }
            else
            {
                buffer.Append(character);
            }
        }

        if (buffer.Length > 0)
        {
            result.Add(buffer.ToString());
        }

        return result;
    }
}

public sealed class NavigationService
{
    private readonly ConfigurationService _configuration;

    public NavigationService(ConfigurationService configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IReadOnlyList<FavoriteDestination> Favorites(Guid scopeId, DestinationKind? kind = null) =>
        _configuration.Favorites.Where(item => item.ScopeId == scopeId && (kind is null || item.Kind == kind)).ToArray();

    public IReadOnlyList<RecentDestination> Recents(Guid scopeId, DestinationKind? kind = null) =>
        _configuration.RecentDestinations.Where(item => item.ScopeId == scopeId && (kind is null || item.Kind == kind)).OrderByDescending(item => item.LastOpened).ToArray();

    public bool AddFavorite(Guid scopeId, DestinationKind kind, string name, string? label = null) =>
        _configuration.AddOrUpdateFavorite(new FavoriteDestination { ScopeId = scopeId, Kind = kind, Name = name, Label = label });

    public bool RemoveFavorite(Guid favoriteId) => _configuration.RemoveFavorite(favoriteId);

    public void RecordRecent(Guid scopeId, DestinationKind kind, string name) =>
        _configuration.RecordRecent(new RecentDestination { ScopeId = scopeId, Kind = kind, Name = name, LastOpened = DateTimeOffset.UtcNow });

    public void ClearRecents(Guid? scopeId = null) => _configuration.ClearRecent(scopeId);
}
