using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace nexIRC.UiAcceptance;

internal sealed class AcceptanceTarget
{
    private AcceptanceTarget(Process process, string executablePath, IntPtr hwnd, AutomationElement window,
        string title, string desktopName, int sessionId, int integrityRid, string telemetryPath)
    {
        Process = process;
        ProcessId = process.Id;
        ExecutablePath = executablePath;
        Hwnd = hwnd;
        Window = window;
        WindowTitle = title;
        DesktopName = desktopName;
        SessionId = sessionId;
        IntegrityRid = integrityRid;
        TelemetryPath = telemetryPath;
    }

    public Process Process { get; }
    public int ProcessId { get; }
    public string ExecutablePath { get; }
    public IntPtr Hwnd { get; }
    public AutomationElement Window { get; }
    public string WindowTitle { get; }
    public string DesktopName { get; }
    public int SessionId { get; }
    public int IntegrityRid { get; }
    public string TelemetryPath { get; }

    public static async Task<AcceptanceTarget> AttachAsync(Process process, string? requestedPath,
        string telemetryPath, int timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(process);
        var expectedPath = Path.GetFullPath(requestedPath ?? throw new ArgumentException("--client is required to validate the target image."));
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException("The selected client process exited during startup.");
            if (process.MainWindowHandle != IntPtr.Zero) break;
            await Task.Delay(100).ConfigureAwait(false);
        }
        process.Refresh();
        if (process.MainWindowHandle == IntPtr.Zero) throw new TimeoutException("The client did not expose a top-level window before the startup deadline.");

        var actualPath = Path.GetFullPath(process.MainModule?.FileName ?? throw new InvalidOperationException("Could not read the target executable path."));
        if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected PID executable does not match --client.");

        var hwnd = FindMainWindow(process.Id);
        if (hwnd == IntPtr.Zero || hwnd != process.MainWindowHandle)
            throw new InvalidOperationException("A unique nexIRC top-level window owned by the selected PID was not found.");
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var hwndPid);
        if (hwndPid != (uint)process.Id) throw new InvalidOperationException("The selected top-level window belongs to a different PID.");

        var title = GetWindowTitle(hwnd);
        if (!string.Equals(title, "nexIRC 5", StringComparison.Ordinal))
            throw new InvalidOperationException("The selected window does not have the expected nexIRC title.");
        var window = AutomationElement.FromHandle(hwnd);
        if (!string.Equals(window.Current.AutomationId, "MainWindow", StringComparison.Ordinal))
            throw new InvalidOperationException("The selected HWND did not expose the expected WPF MainWindow automation id.");
        if (!NativeMethods.IsWindowVisible(hwnd) || !NativeMethods.IsWindowEnabled(hwnd))
            throw new InvalidOperationException("The selected nexIRC main window is not visible and enabled.");

        var targetSession = process.SessionId;
        var currentSession = Process.GetCurrentProcess().SessionId;
        if (targetSession != currentSession) throw new InvalidOperationException("The target and runner are in different Windows sessions.");
        var desktopName = NativeMethods.GetInputDesktopName();
        var threadDesktopName = NativeMethods.GetWindowThreadDesktopName(hwnd);
        if (!string.Equals(desktopName, threadDesktopName, StringComparison.Ordinal))
            throw new InvalidOperationException("The target window is not on the active input desktop.");
        var targetIntegrity = NativeMethods.GetIntegrityRid(process.Id);
        var runnerIntegrity = NativeMethods.GetIntegrityRid(Environment.ProcessId);
        if (targetIntegrity != runnerIntegrity)
            throw new InvalidOperationException("The target and runner integrity levels differ; input will not be injected.");

        return new AcceptanceTarget(process, actualPath, hwnd, window, title, desktopName, targetSession,
            targetIntegrity, Path.GetFullPath(telemetryPath));
    }

    public IReadOnlyList<AutomationElement> FindElements(TreeScope scope, Condition condition, int maximum = 1500)
    {
        var elements = Window.FindAll(scope, condition);
        return elements.Cast<AutomationElement>().Take(maximum).ToArray();
    }

    public void EnsureForeground()
    {
        if (Process.HasExited) throw new InvalidOperationException("The target client exited before input delivery.");
        _ = NativeMethods.GetWindowThreadProcessId(Hwnd, out var windowPid);
        if (windowPid != (uint)ProcessId) throw new InvalidOperationException("The target HWND changed ownership.");
        NativeMethods.ShowWindow(Hwnd, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(Hwnd);
        _ = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var foregroundPid);
        if (foregroundPid != (uint)ProcessId)
            throw new InvalidOperationException("The nexIRC process does not own the foreground window; input was not sent.");
    }

    public bool OwnsPoint(int x, int y)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint(x, y));
        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return threadId != 0 && pid == (uint)ProcessId;
    }

    public int GetPointOwnerPid(int x, int y)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint(x, y));
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return checked((int)pid);
    }

    public int GetForegroundPid()
    {
        _ = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var pid);
        return checked((int)pid);
    }

    public IntPtr[] GetOwnedTopLevelWindows()
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, parameter) =>
        {
            _ = parameter;
            var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (threadId != 0 && pid == (uint)ProcessId) handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return [.. handles];
    }

    public static async Task StopOwnedProcessAsync(Process process, string? expectedPath)
    {
        try
        {
            if (process.HasExited) return;
            var actual = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
            if (expectedPath is null || !string.Equals(actual, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase)) return;
            process.CloseMainWindow();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                process.Refresh();
                if (!process.HasExited) process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static IntPtr FindMainWindow(int processId)
    {
        var matches = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, parameter) =>
        {
            _ = parameter;
            var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (threadId == 0) return true;
            if (pid == (uint)processId && NativeMethods.IsWindowVisible(hwnd)
                && string.Equals(GetWindowTitle(hwnd), "nexIRC 5", StringComparison.Ordinal))
                matches.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return matches.Count == 1 ? matches[0] : IntPtr.Zero;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, Math.Min(length, buffer.Length));
    }
}
