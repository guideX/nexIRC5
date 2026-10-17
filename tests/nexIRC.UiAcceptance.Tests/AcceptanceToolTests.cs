using System.Runtime.InteropServices;
using System.Text.Json;
using nexIRC.UiAcceptance;
using Xunit;

namespace nexIRC.UiAcceptance.Tests;

public sealed class AcceptanceToolTests
{
    [Fact]
    public void NativeInputLayoutsMatchWindowsAbi()
    {
        var expectedMouseInput = Environment.Is64BitProcess ? 32 : 24;
        var expectedKeyboardInput = Environment.Is64BitProcess ? 24 : 16;
        var expectedInput = Environment.Is64BitProcess ? 40 : 28;
        var expectedUnionOffset = Environment.Is64BitProcess ? 8 : 4;

        Assert.Equal(expectedMouseInput, NativeMethods.SizeOfMouseInput);
        Assert.Equal(expectedKeyboardInput, NativeMethods.SizeOfKeyboardInput);
        Assert.Equal(expectedInput, NativeMethods.SizeOfInput);
        Assert.Equal(expectedUnionOffset, Marshal.OffsetOf<NativeMethods.NativeInput>(nameof(NativeMethods.NativeInput.Union)).ToInt32());
    }

    [Fact]
    public void KeyFactorySetsVirtualKeyAndReleaseFlags()
    {
        var input = NativeMethods.NativeInput.Key(NativeMethods.VirtualKeyF24,
            NativeMethods.KeyExtended | NativeMethods.KeyUp);

        Assert.Equal(NativeMethods.InputKeyboard, input.Type);
        Assert.Equal(NativeMethods.VirtualKeyF24, input.Union.Keyboard.VirtualKey);
        Assert.Equal(NativeMethods.KeyExtended | NativeMethods.KeyUp, input.Union.Keyboard.DwFlags);
        Assert.Equal((ushort)0, input.Union.Keyboard.ScanCode);
    }

    [Fact]
    public void ManualModeAcceptsExplicitValidatedTarget()
    {
        var options = AcceptanceOptions.Parse(
        [
            "--mode", "manual", "--pid", "1234", "--client", @"C:\nexIRC\nexIRC.Desktop.exe",
            "--telemetry", @"C:\temp\input.jsonl", "--timeout", "30"
        ]);

        Assert.Equal(AcceptanceMode.Manual, options.Mode);
        Assert.Equal(1234, options.ProcessId);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal(@"C:\temp\input.jsonl", options.TelemetryPath);
    }

    [Fact]
    public void AutomatedModeAcceptsExplicitTestTargetWithTelemetry()
    {
        var options = AcceptanceOptions.Parse(
        [
            "--mode", "automated", "--pid", "1234", "--client", @"C:\nexIRC\nexIRC.Desktop.exe",
            "--telemetry", @"C:\temp\input.jsonl"
        ]);

        Assert.Equal(AcceptanceMode.Automated, options.Mode);
        Assert.Equal(1234, options.ProcessId);
        Assert.Equal(@"C:\temp\input.jsonl", options.TelemetryPath);
    }

    [Fact]
    public void ReportSerializesStructuredStepAndSummaryFields()
    {
        var report = new AcceptanceReport(AcceptanceMode.Diagnostic, DateTimeOffset.UnixEpoch);
        report.Add("UI54-TEST", "TreeItem", 1234, "inspect fixture", "no input", "unchanged", "visible",
            12, "PASS", "Level 2 — Runtime UI Automation", "fingerprint=abcdef123456");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, AcceptanceReport.JsonOptions));
        var step = json.RootElement.GetProperty("Steps")[0];
        Assert.Equal("UI54-TEST", step.GetProperty("ScenarioId").GetString());
        Assert.Equal("PASS", step.GetProperty("Result").GetString());
        Assert.Equal("fingerprint=abcdef123456", step.GetProperty("Details").GetString());
        Assert.Contains("UI54-TEST", report.ToMarkdown(), StringComparison.Ordinal);
    }
}
