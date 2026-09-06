using nexIRC.Core.State;

namespace nexIRC.Application;

/// <summary>
/// A deliberately small, bounded ignore rule.  A rule matches any supplied
/// identity selector (nickname, account, or hostmask) and can be scoped to a
/// saved network profile; a rule with no scope is global.
/// </summary>
public sealed record IgnoreRule
{
    public string? Nickname { get; init; }

    public string? Hostmask { get; init; }

    public string? Account { get; init; }

    public Guid? NetworkProfileId { get; init; }

    public string? NetworkName { get; init; }
}

public sealed record IgnoreIdentity(
    string Nickname,
    string? Username = null,
    string? Host = null,
    string? Account = null,
    Guid? NetworkProfileId = null,
    string? NetworkName = null)
{
    public string? Hostmask => Username is null || Host is null ? null : $"*!{Username}@{Host}";
}

public static class IgnoreMatcher
{
    public static bool Matches(IgnoreRule rule, IgnoreIdentity identity, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(identity);
        if (!MatchesScope(rule, identity))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(rule.Nickname) && IrcCaseMappingComparer.Equals(rule.Nickname, identity.Nickname, mapping))
            || (!string.IsNullOrWhiteSpace(rule.Account) && !string.IsNullOrWhiteSpace(identity.Account) && string.Equals(rule.Account, identity.Account, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(rule.Hostmask) && !string.IsNullOrWhiteSpace(identity.Hostmask) && WildcardEquals(rule.Hostmask, identity.Hostmask));
    }

    public static bool MatchesScope(IgnoreRule rule, IgnoreIdentity identity) =>
        (!rule.NetworkProfileId.HasValue || rule.NetworkProfileId == identity.NetworkProfileId)
        && (string.IsNullOrWhiteSpace(rule.NetworkName) || string.Equals(rule.NetworkName, identity.NetworkName, StringComparison.OrdinalIgnoreCase));

    public static string BuildHostmask(string nickname, string? username, string? host) =>
        string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(host) ? nickname : $"*!{username}@{host}";

    private static bool WildcardEquals(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var star = -1;
        var match = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                star = patternIndex++;
                match = valueIndex;
                continue;
            }

            if (star < 0)
            {
                return false;
            }

            patternIndex = star + 1;
            valueIndex = ++match;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}

public static class IgnoreRuleValidator
{
    public static IgnoreRule? Normalize(IgnoreRule? rule)
    {
        if (rule is null)
        {
            return null;
        }

        var nickname = Normalize(rule.Nickname, ConfigurationLimits.MaximumStringLength);
        var hostmask = Normalize(rule.Hostmask, ConfigurationLimits.MaximumStringLength);
        var account = Normalize(rule.Account, ConfigurationLimits.MaximumStringLength);
        var networkName = Normalize(rule.NetworkName, ConfigurationLimits.MaximumStringLength);
        if (nickname is null && hostmask is null && account is null)
        {
            return null;
        }

        return rule with
        {
            Nickname = nickname,
            Hostmask = hostmask,
            Account = account,
            NetworkName = networkName,
            NetworkProfileId = rule.NetworkProfileId == Guid.Empty ? null : rule.NetworkProfileId
        };
    }

    private static string? Normalize(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength || normalized.Any(character => character is '\r' or '\n')
            ? null
            : normalized;
    }
}
