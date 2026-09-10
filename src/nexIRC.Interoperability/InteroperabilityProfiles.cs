using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Interoperability;

internal enum InteroperabilityScenarioStatus
{
    Passed,
    Failed,
    Unsupported,
    BlockedByServer,
    NotApplicable,
    Inconclusive
}

/// <summary>
/// Data-only endpoint metadata for the shared live IRCv3 scenario harness.
/// Profiles describe setup and expectations; they do not change protocol
/// behavior or duplicate scenario implementations.
/// </summary>
internal sealed record InteroperabilityServerProfile(
    string Name,
    string Implementation,
    string Host,
    int Port,
    bool UseTls,
    string SetupOwnership,
    IReadOnlySet<string> ExpectedOptionalCapabilities,
    string? ClientTagPolicy = null,
    bool HistoryScenarioOptional = true)
{
    public bool IsLocal => Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || Host.Equals("127.0.0.1", StringComparison.Ordinal)
        || Host.Equals("::1", StringComparison.Ordinal);
}

internal static class InteroperabilityProfileCatalog
{
    private static readonly IReadOnlySet<string> CommonOptionalCapabilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            IrcCapabilityCatalog.MessageTags,
            IrcCapabilityCatalog.EchoMessage,
            IrcCapabilityCatalog.ServerTime,
            IrcCapabilityCatalog.AccountTag,
            IrcCapabilityCatalog.Batch,
            IrcCapabilityCatalog.LabeledResponse,
            IrcCapabilityCatalog.Chathistory,
            IrcCapabilityCatalog.EventPlayback
        };

    public static IReadOnlyDictionary<string, InteroperabilityServerProfile> Profiles { get; } =
        new Dictionary<string, InteroperabilityServerProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["ergo-testnet"] = new(
                "ergo-testnet",
                "Ergo",
                "testnet.ergo.chat",
                6697,
                true,
                "external-service",
                CommonOptionalCapabilities),
            ["inspircd-local-full"] = new(
                "inspircd-local-full",
                "InspIRCd",
                "127.0.0.1",
                6667,
                false,
                "phase28-local-launcher",
                CommonOptionalCapabilities,
                "all"),
            ["inspircd-local-noecho"] = new(
                "inspircd-local-noecho",
                "InspIRCd",
                "127.0.0.1",
                6667,
                false,
                "phase28-local-launcher",
                CommonOptionalCapabilities,
                "all"),
            ["inspircd-local-nomsgid"] = new(
                "inspircd-local-nomsgid",
                "InspIRCd",
                "127.0.0.1",
                6667,
                false,
                "phase28-local-launcher",
                CommonOptionalCapabilities,
                "all"),
            ["inspircd-local-restricted-tags"] = new(
                "inspircd-local-restricted-tags",
                "InspIRCd",
                "127.0.0.1",
                6667,
                false,
                "phase28-local-launcher",
                CommonOptionalCapabilities,
                "known"),
            ["inspircd-local-no-tags"] = new(
                "inspircd-local-no-tags",
                "InspIRCd",
                "127.0.0.1",
                6667,
                false,
                "phase28-local-launcher",
                CommonOptionalCapabilities,
                "none"),
            ["inspircd-local-no-message-tags"] = new(
                "inspircd-local-no-message-tags",
                "InspIRCd",
                "127.0.0.1",
                6667,
                false,
                "phase28-local-launcher",
                CommonOptionalCapabilities,
                null),
            ["inspircd-local-no-server-time"] = new(
                "inspircd-local-no-server-time",
                "InspIRCd",
                "127.0.0.1",
                6667,
                false,
                "phase28-local-launcher",
                CommonOptionalCapabilities)
        };

    public static InteroperabilityServerProfile Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Profiles.TryGetValue(name, out var profile))
        {
            return profile;
        }

        var available = string.Join(", ", Profiles.Keys.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));
        throw new ArgumentException($"Unknown interoperability profile '{name}'. Available profiles: {available}.", nameof(name));
    }
}

internal sealed record InteroperabilityScenarioEvidence(
    string Name,
    InteroperabilityScenarioStatus Status,
    string Detail);

internal sealed record Phase28ProfileEvidence(
    string Name,
    string Implementation,
    string ConfiguredHost,
    int ConfiguredPort,
    bool ConfiguredTls,
    string SetupOwnership,
    IReadOnlyList<string> ExpectedOptionalCapabilities,
    string? ConfiguredClientTagPolicy);
