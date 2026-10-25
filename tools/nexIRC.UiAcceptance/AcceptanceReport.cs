using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace nexIRC.UiAcceptance;

internal enum AcceptanceMode { Diagnostic, Automated, Manual }

internal sealed record AcceptanceStep(
    string ScenarioId,
    string ExecutionMode,
    int TargetProcessId,
    string ControlClassification,
    string ActionAttempted,
    string InputDeliveryResult,
    string ApplicationStateAssertion,
    string UiAssertion,
    long ElapsedMilliseconds,
    string Result,
    string EvidenceLevel,
    string Details);

internal sealed class AcceptanceReport(AcceptanceMode mode, DateTimeOffset startedAt)
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string SchemaVersion { get; } = "1.0";
    public AcceptanceMode Mode { get; } = mode;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public DateTimeOffset CompletedAt { get; set; }
    public int TargetProcessId { get; set; }
    public string TargetExecutable { get; set; } = "unknown";
    public string TargetWindowTitle { get; set; } = "unknown";
    public int SessionId { get; set; }
    public string DesktopName { get; set; } = "unknown";
    public int IntegrityRid { get; set; }
    public bool IsAttached { get; set; }
    public List<AcceptanceStep> Steps { get; } = [];
    public List<string> Diagnostics { get; } = [];

    public void Add(string id, string classification, int pid, string action, string input, string state,
        string ui, long elapsed, string result, string evidence = "Level 2 — Runtime UI Automation", string details = "") =>
        Steps.Add(new AcceptanceStep(id, Mode.ToString().ToLowerInvariant(), pid, classification, action, input,
            state, ui, elapsed, result, evidence, details));

    public string ToMarkdown()
    {
        var lines = new List<string>
        {
            "# nexIRC Windows UI Acceptance",
            "",
            $"- Mode: `{Mode.ToString().ToLowerInvariant()}`",
            $"- Target PID: `{TargetProcessId}`; executable: `{TargetExecutable}`; window title verified: `{TargetWindowTitle}`",
            $"- Session: `{SessionId}`; input desktop: `{DesktopName}`; integrity RID: `{IntegrityRid}`",
            $"- Target ownership: `{(IsAttached ? "explicit PID attach" : "runner launched")}`",
            "",
            "| Scenario | Result | Evidence | State assertion | UI assertion |",
            "|---|---|---|---|---|"
        };
        foreach (var step in Steps)
            lines.Add($"| `{step.ScenarioId}` | **{step.Result}** | {step.EvidenceLevel} | {Escape(step.ApplicationStateAssertion)} | {Escape(step.UiAssertion)} |");
        lines.Add("");
        lines.Add("## Diagnostics");
        lines.Add("");
        lines.AddRange(Diagnostics.Count == 0 ? ["No additional diagnostics."] : Diagnostics.Select(static item => $"- {Escape(item)}"));
        lines.Add("");
        lines.Add("Names and input values are omitted from the report; fixture correlation uses short SHA-256 fingerprints.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Escape(string value) => value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
}

internal sealed record AcceptanceOptions(AcceptanceMode Mode, string? ClientPath, string? ReportPath,
    string? TelemetryPath, int? ProcessId, int TimeoutSeconds, int OperatorCheckpointTimeoutSeconds)
{
    public static string DefaultClientPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root, "src", "nexIRC.Desktop", "bin", "Debug", "net10.0-windows", "nexIRC.Desktop.exe");
    }

    public static AcceptanceOptions Parse(string[] args)
    {
        string? modeText = null;
        string? client = null;
        string? report = null;
        string? telemetry = null;
        int? pid = null;
        var timeout = 15;
        var operatorCheckpointTimeout = 900;
        for (var index = 0; index < args.Length; index++)
        {
            string Next() => ++index < args.Length ? args[index] : throw new ArgumentException("Missing option value.");
            switch (args[index])
            {
                case "--mode": modeText = Next(); break;
                case "--client": client = Next(); break;
                case "--report": report = Next(); break;
                case "--telemetry": telemetry = Next(); break;
                case "--pid": pid = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--timeout": timeout = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--checkpoint-timeout": operatorCheckpointTimeout = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }

        if (!Enum.TryParse<AcceptanceMode>(modeText, true, out var mode))
            throw new ArgumentException("--mode must be diagnostic, automated, or manual.");
        if (timeout is < 3 or > 120) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be from 3 to 120 seconds.");
        if (operatorCheckpointTimeout is < 30 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(operatorCheckpointTimeout), "Operator checkpoint timeout must be from 30 to 3600 seconds.");
        if (pid is not null && client is null)
            throw new ArgumentException("Explicit PID attach requires --client to validate its executable path.");
        if (pid is not null && string.IsNullOrWhiteSpace(telemetry))
            throw new ArgumentException("Explicit PID attach requires --telemetry from the target's opt-in probe.");
        return new AcceptanceOptions(mode, client, report, telemetry, pid, timeout, operatorCheckpointTimeout);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nexIRC5.sln"))) return directory.FullName;
        }
        return Environment.CurrentDirectory;
    }
}
