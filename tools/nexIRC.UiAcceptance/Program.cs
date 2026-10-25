using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace nexIRC.UiAcceptance;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This acceptance executable requires Windows.");
            return 2;
        }

        if (args.Length == 0 || args.Any(static argument => argument is "--help" or "-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        AcceptanceOptions options;
        try
        {
            options = AcceptanceOptions.Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            _ = exception;
            Console.Error.WriteLine("Invalid acceptance command line.");
            PrintUsage();
            return 2;
        }

        var expectedClientPath = Path.GetFullPath(options.ClientPath ?? AcceptanceOptions.DefaultClientPath());
        var report = new AcceptanceReport(options.Mode, DateTimeOffset.UtcNow);
        var launchedProcess = false;
        Process? process = null;
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"nexirc-uiacceptance-{Guid.NewGuid():N}");
        var telemetryPath = options.TelemetryPath;
        using var runCancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            runCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        AcceptanceRunner.SetCancellationToken(runCancellation.Token);
        AcceptanceRunner.SetOperatorCheckpointTimeoutSeconds(options.OperatorCheckpointTimeoutSeconds);
        var cancelled = false;

        try
        {
            if (options.ProcessId is int targetPid)
            {
                process = Process.GetProcessById(targetPid);
                if (string.IsNullOrWhiteSpace(telemetryPath))
                    throw new ArgumentException("--telemetry is required when attaching to an explicit --pid.");
                if (!File.Exists(telemetryPath))
                    throw new ArgumentException("The explicit target's opt-in telemetry file does not exist.");
            }
            else
            {
                Directory.CreateDirectory(tempDirectory);
                telemetryPath = Path.Combine(tempDirectory, "input.jsonl");
                process = StartClient(expectedClientPath, telemetryPath);
                launchedProcess = true;
            }

            var target = await AcceptanceTarget.AttachAsync(process, expectedClientPath, telemetryPath!, options.TimeoutSeconds).ConfigureAwait(false);
            report.TargetProcessId = target.ProcessId;
            report.TargetExecutable = Path.GetFileName(target.ExecutablePath);
            report.TargetWindowTitle = target.WindowTitle;
            report.SessionId = target.SessionId;
            report.DesktopName = target.DesktopName;
            report.IntegrityRid = target.IntegrityRid;
            report.IsAttached = !launchedProcess;

            switch (options.Mode)
            {
                case AcceptanceMode.Diagnostic:
                    await AcceptanceRunner.RunDiagnosticAsync(target, report, options.TimeoutSeconds).ConfigureAwait(false);
                    break;
                case AcceptanceMode.Automated:
                    await AcceptanceRunner.RunAutomatedAsync(target, report, options.TimeoutSeconds).ConfigureAwait(false);
                    break;
                case AcceptanceMode.Manual:
                    await AcceptanceRunner.RunManualAsync(target, report, options.TimeoutSeconds).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options.Mode));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report.Add("UI54-RUNNER", "setup", 0, "bounded startup and target validation",
                "runner error", "not evaluated", "not evaluated", 0, "BLOCKED", details: SafeMessage(exception));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            report.Add("UI54-RUNNER-CANCELLED", "cancellation", report.TargetProcessId,
                "cancel acceptance session with Ctrl+C", "cancellation requested", "runner stopping",
                "only the runner-launched process is eligible for cleanup", 0, "NOT RUN",
                details: "Operator canceled the bounded acceptance session.");
        }
        finally
        {
            if (launchedProcess && process is not null)
                await AcceptanceTarget.StopOwnedProcessAsync(process, expectedClientPath).ConfigureAwait(false);
            if (Directory.Exists(tempDirectory))
            {
                try { Directory.Delete(tempDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Console.CancelKeyPress -= cancelHandler;
            AcceptanceRunner.SetCancellationToken(CancellationToken.None);
        }

        report.CompletedAt = DateTimeOffset.UtcNow;
        var reportPath = options.ReportPath ?? Path.Combine(Environment.CurrentDirectory,
            $"nexirc-ui-acceptance-{report.StartedAt:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, AcceptanceReport.JsonOptions)).ConfigureAwait(false);
        var markdownPath = Path.ChangeExtension(reportPath, ".md");
        await File.WriteAllTextAsync(markdownPath, report.ToMarkdown()).ConfigureAwait(false);
        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine($"JSON evidence: {Path.GetFullPath(reportPath)}");
        Console.WriteLine($"Summary: {Path.GetFullPath(markdownPath)}");
        return cancelled ? 130 : report.Steps.Any(static step => step.Result == "FAIL") ? 1
            : report.Steps.Any(static step => step.Result == "BLOCKED") ? 2 : 0;
    }

    private static Process StartClient(string? clientPath, string telemetryPath)
    {
        var path = Path.GetFullPath(clientPath ?? AcceptanceOptions.DefaultClientPath());
        if (!File.Exists(path))
            throw new FileNotFoundException("Build the desktop client or pass --client with its executable path.", path);
        var startInfo = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        startInfo.ArgumentList.Add("--demo");
        startInfo.ArgumentList.Add($"--phase54r3-input-probe={telemetryPath}");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the test-owned desktop client.");
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        FileNotFoundException => "client executable was not found",
        ArgumentException => "acceptance command line is incomplete or invalid",
        InvalidOperationException => "target process or desktop validation failed",
        TimeoutException => "the bounded operator checkpoint or target startup timed out",
        EndOfStreamException => "the operator console closed before the checkpoint completed",
        System.ComponentModel.Win32Exception => "Windows desktop inspection or input delivery was unavailable",
        _ => $"{exception.GetType().Name}: {exception.Message.Replace('\r', ' ').Replace('\n', ' ')}"
    };

    private static void PrintUsage()
    {
        Console.WriteLine("nexIRC.UiAcceptance --mode diagnostic|automated|manual [options]");
        Console.WriteLine("  --client <nexIRC.Desktop.exe>  client path for a test-owned demo process");
        Console.WriteLine("  --pid <pid> --client <path> --telemetry <jsonl>  attach to an explicitly selected opt-in client");
        Console.WriteLine("  --report <path> --timeout <seconds> --checkpoint-timeout <seconds>");
    }
}
