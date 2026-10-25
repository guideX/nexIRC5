using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;

namespace nexIRC.UiAcceptance;

internal static partial class AcceptanceRunner
{
    private const int MaxUiElements = 1500;
    private static readonly Regex ProjectionRegex = new("APP_PIN_PROJECTION count=(?<count>\\d+) order=(?<order>[^\\r\\n]*)", RegexOptions.CultureInvariant);
    private static readonly Regex ViewStateRegex = new("APP_VIEW_STATE phase=(?<phase>before|after) target=(?<target>[a-f0-9]{12}) view=(?<view>[a-f0-9]{12}) active=(?<active>none|[a-f0-9]{12}) open=(?<open>true|false) entries=(?<entries>\\d+) unread=(?<unread>\\d+) important=(?<important>true|false) composerBytes=(?<composerBytes>\\d+) composer=(?<composer>none|[a-f0-9]{12})", RegexOptions.CultureInvariant);
    private static readonly Regex PinnedActivationRegex = new("APP_PINNED_ACTIVATION target=(?<target>[a-f0-9]{12}) view=(?<view>[a-f0-9]{12}) active=(?<active>none|[a-f0-9]{12}) wasOpen=(?<wasOpen>true|false) open=(?<open>true|false) entries=(?<entries>\\d+) unread=(?<unread>\\d+) important=(?<important>true|false) composerBytes=(?<composerBytes>\\d+) composer=(?<composer>none|[a-f0-9]{12})", RegexOptions.CultureInvariant);
    private static string _lastClickDiagnostic = "not evaluated";
    private static CancellationToken _cancellationToken;
    private static int _operatorCheckpointTimeoutSeconds = 900;

    public static void SetCancellationToken(CancellationToken cancellationToken) => _cancellationToken = cancellationToken;

    public static void SetOperatorCheckpointTimeoutSeconds(int seconds) => _operatorCheckpointTimeoutSeconds = seconds;

    public static async Task RunDiagnosticAsync(AcceptanceTarget target, AcceptanceReport report, int timeoutSeconds)
    {
        var started = Stopwatch.StartNew();
        var ptrSize = IntPtr.Size;
        var expectedInput = ptrSize == 8 ? 40 : 28;
        var expectedMouse = ptrSize == 8 ? 32 : 24;
        var expectedKeyboard = ptrSize == 8 ? 24 : 16;
        var layoutOkay = NativeMethods.SizeOfInput == expectedInput
            && NativeMethods.SizeOfMouseInput == expectedMouse
            && NativeMethods.SizeOfKeyboardInput == expectedKeyboard;
        report.Diagnostics.Add($"Input ABI: architecture={(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, INPUT={NativeMethods.SizeOfInput}/{expectedInput}, MOUSEINPUT={NativeMethods.SizeOfMouseInput}/{expectedMouse}, KEYBDINPUT={NativeMethods.SizeOfKeyboardInput}/{expectedKeyboard}; INPUT union offset={Marshal.OffsetOf<NativeMethods.NativeInput>(nameof(NativeMethods.NativeInput.Union))}.");
        var probeFilePresent = File.Exists(target.TelemetryPath);
        var probeBytes = probeFilePresent ? new FileInfo(target.TelemetryPath).Length : 0;
        report.Diagnostics.Add($"Opt-in WPF probe file present={probeFilePresent}; initial bytes={probeBytes}; records={ReadTelemetry(target).Length}.");
        report.Add("UI54-NATIVE-ABI", "native Win32 interop", target.ProcessId, "validate INPUT/native union/MOUSEINPUT/KEYBDINPUT layout",
            $"Marshal.SizeOf matched expected sizes: {layoutOkay}", "structure sizes and union alignment", "x86/x64 sizes recorded", started.ElapsedMilliseconds,
            layoutOkay ? "PASS" : "FAIL", "Level 1 — Native OS input", $"Pointer size {ptrSize}; KEYEVENTF_KEYUP=0x{NativeMethods.KeyUp:X}; KEYEVENTF_EXTENDEDKEY=0x{NativeMethods.KeyExtended:X}.");

        started.Restart();
        var tree = report.IsAttached
            ? FindChannelRows(target, "AlphaNet", "#alpha").FirstOrDefault(static row => !row.Current.IsOffscreen)
            : await WaitForChannelAsync(target, "AlphaNet", "#alpha", timeoutSeconds).ConfigureAwait(false);
        if (tree is null)
        {
            report.Diagnostics.Add(UiaTreeSummary(target));
            report.Add("UI54-UIA-TREE", "WPF TreeView", target.ProcessId, "inspect populated conversation tree", "not sent",
                "fixture row unavailable", "TreeItem was not found", started.ElapsedMilliseconds, "BLOCKED", details: "Demo conversation could not be located through UI Automation.");
            if (report.IsAttached)
            {
                report.Add("UI54-KEY-QUALIFICATION", "attached client — read-only inspection", target.ProcessId,
                    "qualify native key down/up", "not sent", "not changed", "attached PID is inspection-only",
                    started.ElapsedMilliseconds, "NOT RUN", "Level 2 — Runtime UI Automation");
                report.Add("UI54-MOUSE-QUALIFICATION", "attached client — read-only inspection", target.ProcessId,
                    "qualify native mouse down/up", "not sent", "not changed", "attached PID is inspection-only",
                    started.ElapsedMilliseconds, "NOT RUN", "Level 2 — Runtime UI Automation");
            }
            return;
        }
        var rect = tree.Current.BoundingRectangle;
        var point = Center(rect);
        var hit = AutomationElement.FromPoint(new Point(point.X, point.Y));
        var hitClass = ControlClass(hit);
        var owner = target.OwnsPoint(point.X, point.Y);
        var ancestors = GetAncestorClasses(hit, 8);
        var targetName = CurrentName(tree);
        var rowVisible = !rect.IsEmpty && rect.Width > 0 && rect.Height > 0
            && tree.Current.IsEnabled && !tree.Current.IsOffscreen;
        if (report.IsAttached)
        {
            report.Add("UI54-KEY-QUALIFICATION", "attached client — read-only inspection", target.ProcessId,
                "qualify native key down/up", "not sent", "not changed", "attached PID is inspection-only",
                started.ElapsedMilliseconds, "NOT RUN", "Level 2 — Runtime UI Automation");
            report.Add("UI54-MOUSE-QUALIFICATION", "attached client — read-only inspection", target.ProcessId,
                "qualify native mouse down/up", "not sent", "not changed", $"WindowFromPoint target ownership={owner}",
                started.ElapsedMilliseconds, "NOT RUN", "Level 2 — Runtime UI Automation");
            report.Diagnostics.Add($"Read-only attached target row: class={ControlClass(tree)} enabled={tree.Current.IsEnabled} offscreen={tree.Current.IsOffscreen} bounds={RectText(rect)} nameFingerprint={Fingerprint(targetName)}.");
            report.Diagnostics.Add($"WindowFromPoint belongs to attached target PID: {owner}; UIA FromPoint class={hitClass}; UIA ancestry (nearest first): {string.Join(" > ", ancestors)}.");
            report.Add("UI54-UIA-TREE", "WPF TreeView / TreeViewItem", target.ProcessId,
                "inspect populated conversation row without changing focus", "no input sent", "application state unchanged",
                $"visible eligible fixture row={rowVisible}; hit class={hitClass}", started.ElapsedMilliseconds,
                rowVisible ? "PASS" : "FAIL", "Level 2 — Runtime UI Automation",
                "An explicitly attached process is inspected without focus changes or input injection.");
            return;
        }
        var focusedBefore = FocusFingerprint();
        target.EnsureForeground();
        tree.SetFocus();
        await Task.Delay(120).ConfigureAwait(false);
        var keyInput = SendKeyPair(target, NativeMethods.VirtualKeyF24, false);
        await WaitForTelemetryAsync(target, lines => HasMessagePair(lines, "0x0100", "0x0101"), timeoutSeconds).ConfigureAwait(false);
        var keyLines = ReadTelemetry(target);
        var keyDownUp = keyInput.Sent == 2 && HasMessagePair(keyLines, "0x0100", "0x0101");
        report.Add("UI54-KEY-QUALIFICATION", "focused WPF conversation row", target.ProcessId, "send harmless F24 key down then key up",
            $"SendInput {keyInput.Sent}/2; WM_KEYDOWN and WM_KEYUP observed={keyDownUp}",
            "no command issued", "WPF preview key event pair observed=" + HasRecordPair(keyLines, "WPF_PREVIEW_KEY_DOWN", "WPF_PREVIEW_KEY_UP"),
            started.ElapsedMilliseconds, keyDownUp ? "PASS" : "FAIL", "Level 1 — Native OS input",
            "F24 was used to avoid text entry or application commands. Key values are not recorded.");

        started.Restart();
        target.EnsureForeground();
        var mouseInput = SendMousePair(target, point.X, point.Y, rightButton: false);
        await WaitForTelemetryAsync(target, lines => HasMessagePair(lines, "0x0201", "0x0202"), timeoutSeconds).ConfigureAwait(false);
        var mouseLines = ReadTelemetry(target);
        var mouseDownUp = mouseInput.Sent == 2 && HasMessagePair(mouseLines, "0x0201", "0x0202");
        report.Add("UI54-MOUSE-QUALIFICATION", "visible enabled TreeViewItem", target.ProcessId, "send left mouse down then up at UIA row center",
            $"WindowFromPoint owned by target={owner}; SendInput {mouseInput.Sent}/2; WM_LBUTTONDOWN/UP observed={mouseDownUp}",
            "conversation selection only", "WPF preview mouse down/up observed=" + HasRecordPair(mouseLines, "WPF_PREVIEW_MOUSE_DOWN", "WPF_PREVIEW_MOUSE_UP"),
            started.ElapsedMilliseconds, mouseDownUp ? "PASS" : "FAIL", "Level 1 — Native OS input");

        report.Diagnostics.Add($"UIA target row: class={ControlClass(tree)} focusable={tree.Current.IsKeyboardFocusable} enabled={tree.Current.IsEnabled} offscreen={tree.Current.IsOffscreen} bounds={RectText(rect)} nameFingerprint={Fingerprint(targetName)}.");
        report.Diagnostics.Add($"WindowFromPoint belongs to target PID: {owner}; UIA FromPoint class={hitClass}; UIA ancestry (nearest first): {string.Join(" > ", ancestors)}.");
        report.Diagnostics.Add($"WPF route telemetry: {SafeTail(ReadTelemetry(target), 14)}");
        var passed = rowVisible && owner;
        report.Add("UI54-UIA-TREE", "WPF TreeView / TreeViewItem", target.ProcessId, "inspect populated channel row and runtime ancestry",
            "no synthetic WPF event sent", $"eligible fixture row found; nonempty visible bounds={passed}",
            $"hit class={hitClass}; ancestor classes={string.Join(" > ", ancestors)}", started.ElapsedMilliseconds,
            passed ? "PASS" : "FAIL", "Level 2 — Runtime UI Automation",
            "UIA names are represented by short SHA-256 fingerprints; raw names and coordinates are not written to evidence.");
        report.Diagnostics.Add($"Focused element before inspection fingerprint: {focusedBefore}; target DPI={NativeMethods.GetDpiForWindow(target.Hwnd)}.");
    }

    public static async Task RunAutomatedAsync(AcceptanceTarget target, AcceptanceReport report, int timeoutSeconds)
    {
        var fixtureStarted = Stopwatch.StartNew();
        var fixtureReady = report.IsAttached
            ? await WaitForAutomatedFixtureAsync(target, timeoutSeconds).ConfigureAwait(false)
            : await WaitForDemoFixtureAsync(target, timeoutSeconds).ConfigureAwait(false);
        var fixtureScenario = report.IsAttached ? "UI54-ATTACHED-FIXTURE-READY" : "UI54-DEMO-FIXTURE-READY";
        var fixtureDescription = report.IsAttached
            ? "wait for AlphaNet #alpha and AlphaNet #general in the explicitly attached test client"
            : "wait for the demo's seeded channels and historical row";
        report.Add(fixtureScenario, "deterministic test conversation rows", target.ProcessId,
            fixtureDescription,
            "no input sent", "fixture ready=" + fixtureReady,
            report.IsAttached ? "AlphaNet #alpha and AlphaNet #general are available and stable" : "both demo networks and the history row are available and stable",
            fixtureStarted.ElapsedMilliseconds, fixtureReady ? "PASS" : "BLOCKED", "Level 2 — Runtime UI Automation");
        if (!fixtureReady)
        {
            report.Diagnostics.Add(PinnedListSummary(target));
            report.Diagnostics.Add("Bounded demo seed telemetry: " + SafeTail(ReadTelemetry(target), 24));
            AddNotRun(report, "UI54-KEY-QUALIFICATION", "harmless native key transition", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-MOUSE-QUALIFICATION", "native mouse down/up", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-MOUSE-PIN", "mouse pin and reorder journey", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-MOUSE-PIN-BETA", "mouse pin second channel", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-MOUSE-MOVE-TOP", "mouse move to top", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-MOUSE-UNPIN", "mouse unpin", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-PINNED-DESTINATION", "mouse pinned destination activation", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-KEYBOARD-CONTEXT", "keyboard context menu journey", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-KEYBOARD-PIN-CONTEXT", "keyboard channel context menu", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-KEYBOARD-PIN", "keyboard pin journey", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-KEYBOARD-DESTINATION", "keyboard pinned destination activation", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-KEYBOARD-MOVE-TOP", "keyboard move to top", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-KEYBOARD-UNPIN", "keyboard unpin journey", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-KEYBOARD-ACTIONS", "keyboard move and unpin journey", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-KEYBOARD-NAVIGATION", "keyboard navigation", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-PIN-CLOSE-REOPEN", "close and reopen pinned conversation", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-UIA-ACCESSIBILITY", "inspect populated pinned list", "demo fixture did not stabilize");
            AddNotRun(report, "UI54-FOCUS-REMOTE-REORDER", "remote update focus", "isolated demo has no synchronized account");
            AddNotRun(report, "UI54-FOCUS-REMOTE-REMOVE", "remote update focus", "isolated demo has no synchronized account");
            return;
        }
        var keyboardOkay = await QualifyKeyboardAsync(target, report, timeoutSeconds).ConfigureAwait(false);
        var mouseOkay = await QualifyMouseAsync(target, report, timeoutSeconds).ConfigureAwait(false);
        if (!keyboardOkay || !mouseOkay)
        {
            AddNotRunIfMissing(report, "UI54-MOUSE-PIN", "pin a channel by native mouse", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-MOUSE-PIN-BETA", "pin a second channel by native mouse", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-MOUSE-MOVE-TOP", "move a pinned row to top", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-MOUSE-UNPIN", "unpin a row", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-PINNED-DESTINATION", "activate a pinned destination", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-KEYBOARD-CONTEXT", "open pinned context menu using Apps key", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-KEYBOARD-PIN-CONTEXT", "open channel context menu using Apps key", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-KEYBOARD-PIN", "pin a channel using keyboard", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-KEYBOARD-DESTINATION", "activate a pinned destination using keyboard", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-KEYBOARD-MOVE-TOP", "move a pinned destination to top using keyboard", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-KEYBOARD-UNPIN", "unpin a destination using keyboard", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-KEYBOARD-ACTIONS", "native keyboard move and unpin", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-KEYBOARD-NAVIGATION", "continue keyboard navigation", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-PIN-CLOSE-REOPEN", "close and reopen pinned conversation through the UI", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-UIA-ACCESSIBILITY", "inspect populated pinned list", "input qualification failed");
            AddNotRunIfMissing(report, "UI54-FOCUS-REMOTE-REORDER", "remote update focus", "demo fixture is isolated");
            AddNotRunIfMissing(report, "UI54-FOCUS-REMOTE-REMOVE", "remote update focus", "demo fixture is isolated");
            return;
        }
        var alpha = new FixtureConversation("AlphaNet", "#alpha");
        var beta = new FixtureConversation("AlphaNet", "#general");
        var alphaFingerprint = FixtureFingerprint(alpha);
        var betaFingerprint = FixtureFingerprint(beta);

        var alphaRow = ExpandAndFindChannel(target, alpha.Network, alpha.Name);
        if (alphaRow is null || !await EnsureUnpinnedAsync(target, report, "UI54-FIXTURE-UNPIN-ALPHA", alpha, alphaFingerprint, timeoutSeconds).ConfigureAwait(false)
            || !await PinFromTreeAsync(target, report, "UI54-MOUSE-PIN", alpha, alphaFingerprint, timeoutSeconds).ConfigureAwait(false))
            goto MouseFailed;
        var betaRow = ExpandAndFindChannel(target, beta.Network, beta.Name);
        if (betaRow is null || !await EnsureUnpinnedAsync(target, report, "UI54-FIXTURE-UNPIN-BETA", beta, betaFingerprint, timeoutSeconds).ConfigureAwait(false)
            || !await PinFromTreeAsync(target, report, "UI54-MOUSE-PIN-BETA", beta, betaFingerprint, timeoutSeconds).ConfigureAwait(false))
            goto MouseFailed;
        var mouseMoveTarget = report.IsAttached ? alpha : beta;
        var mouseMoveFingerprint = report.IsAttached ? alphaFingerprint : betaFingerprint;
        if (!await MovePinnedToTopAsync(target, report, mouseMoveTarget, mouseMoveFingerprint, timeoutSeconds).ConfigureAwait(false)) goto MouseFailed;
        if (!await ActivatePinnedMouseAsync(target, report, beta, betaFingerprint, timeoutSeconds).ConfigureAwait(false)) goto MouseFailed;
        if (!await CloseReopenAsync(target, report, alpha, alphaFingerprint, timeoutSeconds).ConfigureAwait(false)) goto CloseFailed;
        if (!await KeyboardNavigateAsync(target, report, timeoutSeconds).ConfigureAwait(false)) goto KeyboardFailed;
        if (!await UnpinMouseAsync(target, report, alpha, alphaFingerprint, timeoutSeconds).ConfigureAwait(false)) goto MouseFailed;

        if (!await KeyboardJourneyAsync(target, report, alpha, beta, alphaFingerprint, betaFingerprint, timeoutSeconds).ConfigureAwait(false)) goto KeyboardFailed;

        var accessStarted = Stopwatch.StartNew();
        var pinList = FindByAutomationId(target, "PinnedConversations");
        var rows = GetPinnedButtons(target, pinList).ToArray();
        var alphaFixture = new FixtureConversation("AlphaNet", "#alpha");
        var betaFixture = new FixtureConversation("AlphaNet", "#general");
        var noDuplicate = CountPinnedRows(rows, alphaFixture) <= 1 && CountPinnedRows(rows, betaFixture) <= 1;
        var labeledAndFocusable = pinList is not null && string.Equals(pinList.Current.Name, "Pinned conversations", StringComparison.Ordinal)
            && rows.All(static row => row.Current.IsKeyboardFocusable && row.Current.IsEnabled && !row.Current.IsOffscreen);
        report.Add("UI54-UIA-ACCESSIBILITY", "Pinned conversations List and populated Button rows", target.ProcessId,
            "inspect runtime names, focusability, and duplicate rows", "no input injected",
            $"pinned UI rows={rows.Length}; duplicate fixture rows absent={noDuplicate}",
            $"named list and enabled/focusable visible rows={labeledAndFocusable}", accessStarted.ElapsedMilliseconds,
            noDuplicate && labeledAndFocusable ? "PASS" : "FAIL", "Level 2 — Runtime UI Automation");
        AddNotRun(report, "UI54-FOCUS-REMOTE-REORDER", "focus after a second-client synchronized MOVE_TOP",
            report.IsAttached ? "automated mode does not perform the second client's remote mutation; use attached manual mode" : "the isolated demo has no authenticated synchronized-pin account");
        AddNotRun(report, "UI54-FOCUS-REMOTE-REMOVE", "focus after a second-client synchronized UNPIN",
            report.IsAttached ? "automated mode does not perform the second client's remote mutation; use attached manual mode" : "the isolated demo has no authenticated synchronized-pin account");
        return;

    MouseFailed:
        AddNotRunIfMissing(report, "UI54-MOUSE-PIN", "pin a channel by native mouse", "mouse journey stopped before this scenario");
        AddNotRunIfMissing(report, "UI54-MOUSE-PIN-BETA", "pin a second channel by native mouse", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-MOUSE-MOVE-TOP", "move a pinned row to top", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-MOUSE-UNPIN", "unpin a row", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-PINNED-DESTINATION", "activate a pinned destination", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-CONTEXT", "open pinned context menu using Apps key", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-PIN-CONTEXT", "open channel context menu using Apps key", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-PIN", "pin a channel using keyboard", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-DESTINATION", "activate a pinned destination using keyboard", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-MOVE-TOP", "move a pinned destination to top using keyboard", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-UNPIN", "unpin a destination using keyboard", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-ACTIONS", "native keyboard move and unpin", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-NAVIGATION", "continue keyboard navigation", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-PIN-CLOSE-REOPEN", "close and reopen pinned conversation through the UI", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-UIA-ACCESSIBILITY", "inspect populated pinned list", "mouse journey did not establish a pinned fixture");
        AddNotRunIfMissing(report, "UI54-FOCUS-REMOTE-REORDER", "focus after remote MOVE_TOP", "isolated demo has no synchronized account");
        AddNotRunIfMissing(report, "UI54-FOCUS-REMOTE-REMOVE", "focus after remote UNPIN", "isolated demo has no synchronized account");
        return;
    KeyboardFailed:
        AddNotRunIfMissing(report, "UI54-KEYBOARD-PIN-CONTEXT", "open channel context menu using Apps key", "keyboard journey stopped before this scenario");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-PIN", "pin a channel using keyboard", "keyboard journey did not establish this scenario");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-DESTINATION", "activate a pinned destination using keyboard", "keyboard journey did not establish this scenario");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-CONTEXT", "open pinned context menu using Apps key", "keyboard journey did not establish this scenario");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-MOVE-TOP", "move a pinned destination to top using keyboard", "keyboard journey did not establish this scenario");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-UNPIN", "unpin a destination using keyboard", "keyboard journey did not establish this scenario");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-ACTIONS", "native keyboard move and unpin", "keyboard journey did not complete");
        AddNotRunIfMissing(report, "UI54-KEYBOARD-NAVIGATION", "continue keyboard navigation", "keyboard journey did not complete");
        AddNotRunIfMissing(report, "UI54-PIN-CLOSE-REOPEN", "close and reopen pinned conversation through the UI", "keyboard journey did not complete");
        AddNotRunIfMissing(report, "UI54-UIA-ACCESSIBILITY", "inspect populated pinned list", "keyboard journey did not complete");
        AddNotRunIfMissing(report, "UI54-FOCUS-REMOTE-REORDER", "focus after remote MOVE_TOP", "isolated demo has no synchronized account");
        AddNotRunIfMissing(report, "UI54-FOCUS-REMOTE-REMOVE", "focus after remote UNPIN", "isolated demo has no synchronized account");
        return;
    CloseFailed:
        AddNotRunIfMissing(report, "UI54-UIA-ACCESSIBILITY", "inspect populated pinned list", "close/reopen journey did not complete");
        AddNotRunIfMissing(report, "UI54-FOCUS-REMOTE-REORDER", "focus after remote MOVE_TOP", "isolated demo has no synchronized account");
        AddNotRunIfMissing(report, "UI54-FOCUS-REMOTE-REMOVE", "focus after remote UNPIN", "isolated demo has no synchronized account");
    }

    public static async Task RunManualAsync(AcceptanceTarget target, AcceptanceReport report, int timeoutSeconds)
    {
        if (Console.IsInputRedirected)
        {
            report.Add("UI54-MANUAL-START", "operator checkpoint", target.ProcessId, "start guided manual acceptance",
                "console input is redirected", "not evaluated", "operator checkpoint unavailable", 0, "BLOCKED", "Level 1 — Manual witnessed OS input");
            return;
        }

        if (report.IsAttached)
        {
            await RunAttachedManualAsync(target, report, timeoutSeconds).ConfigureAwait(false);
            return;
        }

        Console.WriteLine("The client is a test-owned demo fixture. Each checkpoint needs the actual mouse/keyboard action and the matching WPF plus UIA/state evidence; pressing Enter alone never passes a step.");
        Console.WriteLine("Fixture rows: AlphaNet / #alpha and AlphaNet / #general. This isolated demo cannot produce remote synchronized updates.");
        if (!await WaitForDemoFixtureAsync(target, timeoutSeconds).ConfigureAwait(false))
        {
            report.Add("UI54-MANUAL-START", "conversation TreeItem", target.ProcessId, "wait for demo fixture", "not sent",
                "fixture row or seeded pin projection unavailable", "UIA tree or projection did not stabilize", 0, "BLOCKED", details: UiaTreeSummary(target));
            return;
        }
        var alpha = new FixtureConversation("AlphaNet", "#alpha");
        var beta = new FixtureConversation("AlphaNet", "#general");
        var alphaHash = FixtureFingerprint(alpha);
        var betaHash = FixtureFingerprint(beta);
        var initialAlphaPinned = HasPinnedRow(target, alpha);
        var initialBetaPinned = HasPinnedRow(target, beta);
        report.Add("UI54-MANUAL-FIXTURE", "demo conversation tree and pinned List", target.ProcessId,
            "inspect deterministic initial fixture", "no input sent", $"Alpha pinned={initialAlphaPinned}; Beta pinned={initialBetaPinned}",
            $"UIA tree ready; pinned projection={string.Join(',', ProjectionOrder(target))}", 0,
            initialAlphaPinned && !initialBetaPinned ? "PASS" : "BLOCKED", "Level 2 — Runtime UI Automation");
        if (!initialAlphaPinned || initialBetaPinned)
        {
            AddNotRun(report, "UI54-MOUSE-PIN", "pin AlphaNet #alpha", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-MOUSE-PIN-BETA", "pin AlphaNet #general", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-MOUSE-MOVE-TOP", "move AlphaNet #alpha to top", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-MOUSE-UNPIN", "unpin AlphaNet #alpha", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-PINNED-DESTINATION", "activate AlphaNet #general", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-KEYBOARD-NAVIGATION", "navigate with keyboard", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-KEYBOARD-CONTEXT", "open a channel context menu with keyboard", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-KEYBOARD-PIN", "pin AlphaNet #alpha with keyboard", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-KEYBOARD-DESTINATION", "activate a pinned conversation with keyboard", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-KEYBOARD-MOVE-TOP", "move a pinned conversation with keyboard", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-KEYBOARD-UNPIN", "unpin a conversation with keyboard", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-KEYBOARD-ACTIONS", "keyboard Move to top and Unpin", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-PIN-CLOSE-REOPEN", "close and reopen a pinned view", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-UIA-ACCESSIBILITY", "inspect populated pinned list", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-FOCUS-REMOTE-REORDER", "remote reorder focus", "manual demo did not start in its documented fixture state");
            AddNotRun(report, "UI54-FOCUS-REMOTE-REMOVE", "remote removal focus", "manual demo did not start in its documented fixture state");
            return;
        }

        await ManualCheckpointAsync(target, report, "UI54-MANUAL-FIXTURE-UNPIN",
            "Right-click the pre-pinned AlphaNet #alpha button and choose Unpin conversation to normalize the demo.",
            "APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation", alphaHash,
            () => !HasPinnedRow(target, alpha) && HasPinnedRow(target, beta) == initialBetaPinned, timeoutSeconds,
            requirePreservation: true).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-MOUSE-PIN", "Right-click AlphaNet #alpha in the conversation tree and choose Pin conversation.",
            "APP_CONTEXT_MENU_ACTION id=ContextAction.Pinconversation", alphaHash,
            () => HasPinnedRow(target, alpha) && CountPinnedRows(target, alpha) == 1
                && ProjectionOrder(target).Contains(alphaHash, StringComparer.Ordinal), timeoutSeconds,
            requirePreservation: true).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-MOUSE-PIN-BETA", "Right-click AlphaNet #general and choose Pin conversation.",
            "APP_CONTEXT_MENU_ACTION id=ContextAction.Pinconversation", betaHash,
            () => HasPinnedRow(target, beta) && CountPinnedRows(target, beta) == 1
                && ProjectionOrder(target).Contains(betaHash, StringComparer.Ordinal), timeoutSeconds,
            requirePreservation: true).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-MOUSE-MOVE-TOP", "Right-click the pinned AlphaNet #general row and choose Move to top.",
            "APP_CONTEXT_MENU_ACTION id=ContextAction.Movetotop", betaHash,
            () => IsFirstPinned(target, beta) && CountPinnedRows(target, beta) == 1, timeoutSeconds,
            requirePreservation: true).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-MOUSE-UNPIN", "Right-click pinned AlphaNet #alpha and choose Unpin conversation.",
            "APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation", alphaHash,
            () => !HasPinnedRow(target, alpha) && HasPinnedRow(target, beta)
                && !ProjectionOrder(target).Contains(alphaHash, StringComparer.Ordinal), timeoutSeconds,
            requirePreservation: true).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-PINNED-DESTINATION", "Click AlphaNet #general in Pinned to activate the destination.",
            "APP_PINNED_ACTIVATION", betaHash,
            () => ReadTelemetry(target).Any(line => line.Contains($"APP_PINNED_ACTIVATION target={betaHash}", StringComparison.Ordinal)
                && line.Contains("open=true", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        await ManualFocusCheckpointAsync(target, report, timeoutSeconds).ConfigureAwait(false);

        await ManualCheckpointAsync(target, report, "UI54-KEYBOARD-CONTEXT",
            "Focus AlphaNet #alpha in the conversation list and open its context menu with Apps or Shift+F10. Leave the menu open.",
            "APP_CONTEXT_MENU_OPENED id=ChannelContextMenu", string.Empty,
            () => ElementsForProcess(target).Any(element => element.Current.ControlType == ControlType.Menu
                && string.Equals(element.Current.AutomationId, "ChannelContextMenu", StringComparison.Ordinal)
                && !element.Current.IsOffscreen), timeoutSeconds).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-KEYBOARD-PIN", "Use only the keyboard to select Pin conversation from the open channel context menu.",
            "APP_CONTEXT_MENU_ACTION id=ContextAction.Pinconversation", alphaHash,
            () => HasPinnedRow(target, alpha) && CountPinnedRows(target, alpha) == 1
                && ProjectionOrder(target).Contains(alphaHash, StringComparer.Ordinal), timeoutSeconds,
            requirePreservation: true).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-KEYBOARD-DESTINATION",
            "Use Tab and arrow keys to focus AlphaNet #alpha in Pinned, then press Enter to activate it.",
            "APP_PINNED_ACTIVATION", alphaHash,
            () => ReadTelemetry(target).Any(line => line.Contains($"APP_PINNED_ACTIVATION target={alphaHash}", StringComparison.Ordinal)
                && line.Contains("open=true", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-KEYBOARD-MOVE-TOP",
            "With AlphaNet #alpha focused in Pinned, open its context menu using Apps or Shift+F10 and choose Move to top with the keyboard.",
            "APP_CONTEXT_MENU_ACTION id=ContextAction.Movetotop", alphaHash,
            () => IsFirstPinned(target, alpha) && CountPinnedRows(target, alpha) == 1, timeoutSeconds,
            requirePreservation: true).ConfigureAwait(false);
        await ManualCheckpointAsync(target, report, "UI54-KEYBOARD-UNPIN",
            "Keep AlphaNet #general focused, open its context menu with Apps or Shift+F10, and choose Unpin conversation using the keyboard.",
            "APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation", betaHash,
            () => !HasPinnedRow(target, beta) && HasPinnedRow(target, alpha)
                && !ProjectionOrder(target).Contains(betaHash, StringComparer.Ordinal), timeoutSeconds,
            requirePreservation: true).ConfigureAwait(false);
        var keyboardMove = report.Steps.LastOrDefault(static step => step.ScenarioId == "UI54-KEYBOARD-MOVE-TOP")?.Result == "PASS";
        var keyboardUnpin = report.Steps.LastOrDefault(static step => step.ScenarioId == "UI54-KEYBOARD-UNPIN")?.Result == "PASS";
        report.Add("UI54-KEYBOARD-ACTIONS", "pinned conversation ContextMenu", target.ProcessId,
            "complete manual keyboard Move to top and Unpin actions", $"Move to top={keyboardMove}; Unpin={keyboardUnpin}",
            $"Alpha remains pinned={HasPinnedRow(target, alpha)}; Beta removed={!HasPinnedRow(target, beta)}",
            "Both actions require fresh keyboard event, WPF action telemetry, target fingerprint, and state assertion.", 0,
            keyboardMove && keyboardUnpin ? "PASS" : "NOT RUN", "Level 1 — Manual witnessed OS input");

        await ManualCloseReopenCheckpointAsync(target, report, alpha, alphaHash, timeoutSeconds).ConfigureAwait(false);
        RecordManualAccessibility(target, report, alphaHash, betaHash);

        if (!report.IsAttached)
        {
            AddNotRun(report, "UI54-FOCUS-REMOTE-REORDER", "in a second authenticated client, MOVE_TOP the focused pinned item and inspect focus here", "runner-launched demo fixture is not account synchronized");
            AddNotRun(report, "UI54-FOCUS-REMOTE-REMOVE", "in a second authenticated client, UNPIN the focused item and inspect focus here", "runner-launched demo fixture is not account synchronized");
        }
        else
        {
            await ManualRemoteFocusAsync(target, report, "UI54-FOCUS-REMOTE-REORDER", "On the second authenticated client, MOVE_TOP a different pinned conversation. Keep the original pinned row focused here and wait for the order to change.", timeoutSeconds).ConfigureAwait(false);
            await ManualRemoteFocusAsync(target, report, "UI54-FOCUS-REMOTE-REMOVE", "On the second authenticated client, UNPIN the currently focused conversation. Confirm it disappears here and focus moves to a remaining control.", timeoutSeconds).ConfigureAwait(false);
        }
    }

    private static async Task RunAttachedManualAsync(AcceptanceTarget target, AcceptanceReport report, int timeoutSeconds)
    {
        Console.WriteLine("Manual mode is attached to an explicitly selected client. This path sends no input and does not launch, close, or modify another process.");
        Console.WriteLine("Remote focus checks require two authenticated clients on the same account, with at least two pinned conversations in the attached client.");
        var list = FindByAutomationId(target, "PinnedConversations");
        var initialRows = GetPinnedButtons(target, list).ToArray();
        var initialOrder = ProjectionOrder(target);
        var initialReady = list is not null && initialRows.Length >= 2;
        report.Add("UI54-REMOTE-FOCUS-FIXTURE", "populated pinned List", target.ProcessId,
            "inspect attached two-client fixture preconditions", "no input sent",
            $"pinned UIA rows={initialRows.Length}; projection items={initialOrder.Length}",
            $"named accessible list={list is not null && CurrentName(list) == "Pinned conversations"}; focus must be set by operator",
            0, initialReady ? "PASS" : "BLOCKED", "Level 2 — Runtime UI Automation",
            "The attached process is read-only to the runner; no input was injected.");

        foreach (var (id, action) in new[]
        {
            ("UI54-MOUSE-PIN", "mouse PIN on demo fixture"),
            ("UI54-MOUSE-PIN-BETA", "mouse PIN of second demo row"),
            ("UI54-MOUSE-MOVE-TOP", "mouse MOVE_TOP on demo fixture"),
            ("UI54-MOUSE-UNPIN", "mouse UNPIN on demo fixture"),
            ("UI54-PINNED-DESTINATION", "mouse activation on demo fixture"),
            ("UI54-KEYBOARD-NAVIGATION", "keyboard navigation on demo fixture"),
            ("UI54-KEYBOARD-CONTEXT", "keyboard context menu on demo fixture"),
            ("UI54-KEYBOARD-PIN", "keyboard PIN on demo fixture"),
            ("UI54-KEYBOARD-DESTINATION", "keyboard activation on demo fixture"),
            ("UI54-KEYBOARD-MOVE-TOP", "keyboard MOVE_TOP on demo fixture"),
            ("UI54-KEYBOARD-UNPIN", "keyboard UNPIN on demo fixture"),
            ("UI54-KEYBOARD-ACTIONS", "keyboard mutation journey on demo fixture"),
            ("UI54-PIN-CLOSE-REOPEN", "close/reopen on demo fixture")
        })
            AddNotRun(report, id, action, "explicitly attached remote-focus session does not alter the local demo or exercise this fixture journey");

        if (!initialReady)
        {
            AddNotRun(report, "UI54-FOCUS-REMOTE-REORDER", "remote MOVE_TOP and retained focus", "two pinned conversations are required");
            AddNotRun(report, "UI54-FOCUS-REMOTE-REMOVE", "remote UNPIN and focus restoration", "two pinned conversations are required");
            report.Add("UI54-UIA-ACCESSIBILITY", "attached populated pinned List", target.ProcessId,
                "inspect accessible pinned items", "no input sent", "precondition unavailable", PinnedListSummary(target),
                0, "BLOCKED", "Level 2 — Runtime UI Automation");
            return;
        }

        await ManualRemoteFocusAsync(target, report, "UI54-FOCUS-REMOTE-REORDER",
            "In client A, focus a pinned row that is not currently first. In client B, move that exact conversation to the top, wait for A to update, then use the keyboard once in A.",
            timeoutSeconds).ConfigureAwait(false);
        await ManualRemoteFocusAsync(target, report, "UI54-FOCUS-REMOTE-REMOVE",
            "In client A, focus a pinned row. In client B, unpin that exact conversation. Wait for A to remove it and move focus to a remaining pinned row, then use the keyboard once in A.",
            timeoutSeconds).ConfigureAwait(false);

        list = FindByAutomationId(target, "PinnedConversations");
        var rows = GetPinnedButtons(target, list).ToArray();
        var names = rows.Select(row => Fingerprint(CurrentName(row))).ToArray();
        var uniqueRows = names.Distinct(StringComparer.Ordinal).Count() == names.Length;
        var accessible = list is not null && CurrentName(list) == "Pinned conversations"
            && rows.All(static row => row.Current.IsEnabled && row.Current.IsKeyboardFocusable && !row.Current.IsOffscreen);
        var projection = ProjectionOrder(target);
        report.Add("UI54-UIA-ACCESSIBILITY", "attached populated pinned List and Button rows", target.ProcessId,
            "inspect post-mutation names, focusability, visibility, membership, and duplicate rows",
            "no input sent", $"initial UIA rows={initialRows.Length}; current UIA rows={rows.Length}; unique accessible rows={uniqueRows}",
            $"named/focusable/enabled/visible controls={accessible}; model projection count={projection.Length}; row-name fingerprints={string.Join(',', names)}",
            0, accessible && uniqueRows && rows.Length == projection.Length ? "PASS" : "FAIL",
            "Level 2 — Runtime UI Automation",
            "Names are fingerprinted; the remote mutations were operator-triggered from a second client.");
    }

    private static async Task<bool> QualifyKeyboardAsync(AcceptanceTarget target, AcceptanceReport report, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var row = await WaitForChannelAsync(target, "AlphaNet", "#alpha", timeoutSeconds).ConfigureAwait(false);
        if (row is null)
        {
            report.Add("UI54-KEY-QUALIFICATION", "conversation TreeItem", target.ProcessId, "send harmless native key transition", "target row missing", "not evaluated", "not evaluated", watch.ElapsedMilliseconds, "BLOCKED", "Level 1 — Native OS input");
            return false;
        }
        target.EnsureForeground();
        row.SetFocus();
        var before = ReadTelemetry(target).Length;
        var result = SendKeyPair(target, NativeMethods.VirtualKeyF24, false);
        await WaitForTelemetryAsync(target, lines => lines.Skip(before).Any(static line => line == "WPF_PREVIEW_KEY_UP"), timeoutSeconds).ConfigureAwait(false);
        var delta = ReadTelemetry(target).Skip(before).ToArray();
        var valid = result.Sent == 2 && HasMessagePair(delta, "0x0100", "0x0101")
            && HasRecordPair(delta, "WPF_PREVIEW_KEY_DOWN", "WPF_PREVIEW_KEY_UP");
        report.Add("UI54-KEY-QUALIFICATION", "focused WPF conversation row", target.ProcessId,
            "send harmless F24 key down and key up", $"SendInput {result.Sent}/2; WM key pair={HasMessagePair(delta, "0x0100", "0x0101")}",
            "no command or text entry", $"WPF preview key pair={HasRecordPair(delta, "WPF_PREVIEW_KEY_DOWN", "WPF_PREVIEW_KEY_UP")}",
            watch.ElapsedMilliseconds, valid ? "PASS" : "FAIL", "Level 1 — Native OS input", "F24 only; no key values are logged.");
        return valid;
    }

    private static async Task<bool> QualifyMouseAsync(AcceptanceTarget target, AcceptanceReport report, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var row = await WaitForChannelAsync(target, "AlphaNet", "#alpha", timeoutSeconds).ConfigureAwait(false);
        if (row is null)
        {
            report.Add("UI54-MOUSE-QUALIFICATION", "conversation TreeItem", target.ProcessId, "send left mouse down/up", "target row missing", "not evaluated", "not evaluated", watch.ElapsedMilliseconds, "BLOCKED", "Level 1 — Native OS input");
            return false;
        }
        var point = ClickPoint(row);
        if (!target.OwnsPoint(point.X, point.Y))
        {
            report.Add("UI54-MOUSE-QUALIFICATION", ControlClass(row), target.ProcessId, "send left mouse down/up",
                "WindowFromPoint did not map to target PID", "no input delivered", "not evaluated", watch.ElapsedMilliseconds,
                "BLOCKED", "Level 1 — Native OS input",
                $"target row visible={!row.Current.IsOffscreen}; bounds={RectText(row.Current.BoundingRectangle)}; point owner PID={target.GetPointOwnerPid(point.X, point.Y)}; foreground PID={target.GetForegroundPid()}");
            return false;
        }
        target.EnsureForeground();
        var before = ReadTelemetry(target).Length;
        var result = SendMousePair(target, point.X, point.Y, rightButton: false);
        await WaitForTelemetryAsync(target, lines => lines.Skip(before).Any(static line => line == "WPF_PREVIEW_MOUSE_UP"), timeoutSeconds).ConfigureAwait(false);
        var delta = ReadTelemetry(target).Skip(before).ToArray();
        var valid = result.Sent == 2 && HasMessagePair(delta, "0x0201", "0x0202")
            && HasRecordPair(delta, "WPF_PREVIEW_MOUSE_DOWN", "WPF_PREVIEW_MOUSE_UP");
        report.Add("UI54-MOUSE-QUALIFICATION", ControlClass(row), target.ProcessId,
            "send native left mouse down and up at the UIA row center", $"WindowFromPoint PID verified; SendInput {result.Sent}/2",
            "conversation selection only", $"WPF preview mouse pair={HasRecordPair(delta, "WPF_PREVIEW_MOUSE_DOWN", "WPF_PREVIEW_MOUSE_UP")}",
            watch.ElapsedMilliseconds, valid ? "PASS" : "FAIL", "Level 1 — Native OS input");
        return valid;
    }

    private static async Task<bool> PinFromTreeAsync(AcceptanceTarget target, AcceptanceReport report, string scenario,
        FixtureConversation fixture, string fingerprint, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var row = ExpandAndFindChannel(target, fixture.Network, fixture.Name);
        if (row is null) return AddBlocked(report, scenario, "TreeItem", "eligible conversation row not found");
        var menuResult = await OpenContextMenuAsync(target, row, "ChannelContextMenu", timeoutSeconds).ConfigureAwait(false);
        if (!menuResult.Opened)
            return AddFail(report, scenario, ControlClass(row), "native right-click actual row and choose Pin", menuResult.Details, "application catalog unchanged", "Pin action missing", SafeTail(ReadTelemetry(target), 32));
        var action = FindMenuAction(target, menuResult.Menu, "ContextAction.Pinconversation");
        if (action is null)
            return AddFail(report, scenario, ControlClass(row), "native click Pin conversation", "menu action not found by UIA",
                "catalog not confirmed", "context action not invoked", MenuItemSummary(target));
        var telemetryStart = ReadTelemetry(target).Length;
        if (!await ClickElementAsync(target, action, rightButton: false).ConfigureAwait(false))
            return AddFail(report, scenario, ControlClass(row), "native click Pin conversation", _lastClickDiagnostic,
                "catalog not confirmed", "context action not invoked", MenuItemSummary(target));
        var state = await WaitUntilAsync(() => HasPinnedRow(target, fixture), timeoutSeconds).ConfigureAwait(false);
        var stateLines = await WaitForTelemetryAsync(target, lines => lines.Skip(telemetryStart).Any(line =>
            line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Pinconversation target={fingerprint}", StringComparison.Ordinal))
            && lines.Skip(telemetryStart).Any(line => line.Contains($"APP_VIEW_STATE phase=after target={fingerprint}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        var newEvents = stateLines.Skip(telemetryStart).ToArray();
        var actualAction = newEvents.Any(line => line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Pinconversation target={fingerprint}", StringComparison.Ordinal));
        var projection = ProjectionOrder(target);
        var appState = state && projection.Contains(fingerprint, StringComparer.Ordinal);
        var hasStatePair = TryGetViewStatePair(newEvents, fingerprint, out var beforeState, out var afterState);
        var preserved = hasStatePair && SameConversationData(beforeState, afterState, compareComposer: true);
        var activePreserved = hasStatePair && beforeState.ActiveFingerprint == afterState.ActiveFingerprint;
        report.Add(scenario, "WPF channel TreeItem", target.ProcessId, "right-click row and click Pin conversation using native SendInput",
            "right-button down/up, WPF menu open, left-button down/up on Pin", $"pinned projection contains target={appState}; count={projection.Length}",
            $"menu action target fingerprint correlated={actualAction}; UIA pinned row present={state}; transcript/unread/important/composer preserved={preserved}; active selection preserved={activePreserved}", watch.ElapsedMilliseconds,
            state && appState && actualAction && preserved && activePreserved ? "PASS" : "FAIL", "Level 1 — Native OS input",
            $"real WPF menu action and catalog projection observed; state pair={hasStatePair}; before={FormatConversationState(beforeState)}; after={FormatConversationState(afterState)}");
        return state && appState && actualAction && preserved && activePreserved;
    }

    private static async Task<bool> EnsureUnpinnedAsync(AcceptanceTarget target, AcceptanceReport report, string scenario,
        FixtureConversation fixture, string fingerprint, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var row = ExpandAndFindChannel(target, fixture.Network, fixture.Name);
        if (row is null) return AddBlocked(report, scenario, "TreeItem", "pre-pinned fixture row not found");
        var menu = await OpenContextMenuAsync(target, row, "ChannelContextMenu", timeoutSeconds).ConfigureAwait(false);
        if (!menu.Opened)
            return AddFail(report, scenario, "WPF channel TreeItem", "inspect initial fixture pin state",
                menu.Details, "fixture state not established", "channel context menu did not open");
        if (FindMenuAction(target, menu.Menu, "ContextAction.Pinconversation") is not null)
        {
            _ = SendKeyPair(target, NativeMethods.VirtualKeyEscape, extended: false);
            report.Add(scenario, "WPF channel TreeItem", target.ProcessId,
                "verify fixture starts unpinned and dismiss its menu", "native Escape key down/up",
                "Pin action available; Unpin action absent", "fixture is not pinned",
                watch.ElapsedMilliseconds, "PASS", "Level 1 — Native OS input");
            return true;
        }
        var action = FindMenuAction(target, menu.Menu, "ContextAction.Unpinconversation");
        if (action is null || !await ClickElementAsync(target, action, rightButton: false).ConfigureAwait(false))
            return AddFail(report, scenario, "WPF channel TreeItem", "remove pre-existing fixture pin through the native context menu",
                "Unpin menu action unavailable", "fixture pin remains", "Unpin action was not invoked");
        var eventLines = await WaitForTelemetryAsync(target, lines => lines.Any(line => line.Contains(
            $"APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation target={fingerprint}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        var eventObserved = eventLines.Any(line => line.Contains(
            $"APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation target={fingerprint}", StringComparison.Ordinal));
        var normalizedMenu = await OpenContextMenuAsync(target, row, "ChannelContextMenu", timeoutSeconds).ConfigureAwait(false);
        var removed = normalizedMenu.Opened && FindMenuAction(target, normalizedMenu.Menu, "ContextAction.Pinconversation") is not null
            && FindMenuAction(target, normalizedMenu.Menu, "ContextAction.Unpinconversation") is null;
        var projection = ProjectionOrder(target);
        _ = SendKeyPair(target, NativeMethods.VirtualKeyEscape, extended: false);
        report.Add(scenario, "WPF channel TreeItem", target.ProcessId,
            "normalize demo fixture by removing its initial pin through native menu input", "native right-click and left-click",
            $"pre-existing pin removed={removed}; projection contains target={projection.Contains(fingerprint, StringComparer.Ordinal)}",
            $"Unpin action target correlated={eventObserved}", watch.ElapsedMilliseconds,
            removed && eventObserved && !projection.Contains(fingerprint, StringComparer.Ordinal) ? "PASS" : "FAIL", "Level 1 — Native OS input",
            SafeTail(ReadTelemetry(target), 24));
        return removed && eventObserved && !projection.Contains(fingerprint, StringComparer.Ordinal);
    }

    private static async Task<bool> MovePinnedToTopAsync(AcceptanceTarget target, AcceptanceReport report,
        FixtureConversation fixture, string fingerprint, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var row = FindPinnedButton(target, fixture);
        if (row is null) return AddBlocked(report, "UI54-MOUSE-MOVE-TOP", "Pinned Button", "target pinned row not found");
        var menuResult = await OpenContextMenuAsync(target, row, "PinnedConversationContextMenu", timeoutSeconds).ConfigureAwait(false);
        if (!menuResult.Opened)
            return AddFail(report, "UI54-MOUSE-MOVE-TOP", "Pinned Button", "open pinned row context menu", "context menu did not open", "order unchanged", "menu action missing");
        var action = FindMenuAction(target, menuResult.Menu, "ContextAction.Movetotop");
        var telemetryStart = ReadTelemetry(target).Length;
        var orderBefore = ProjectionOrder(target);
        var indexBefore = Array.IndexOf(orderBefore, fingerprint);
        if (action is null)
        {
            var expectedForTarget = report.IsAttached ? "the synchronized fixture must expose its non-first row Move to top action" : "the demo fixture row did not expose Move to top";
            return AddFail(report, "UI54-MOUSE-MOVE-TOP", "Pinned Button / ContextMenu", "inspect Move to top availability",
                "no menu action to click", "order unchanged", $"{expectedForTarget}; {MenuItemSummary(target)}");
        }
        if (!await ClickElementAsync(target, action, false).ConfigureAwait(false))
            return AddFail(report, "UI54-MOUSE-MOVE-TOP", "Pinned Button / ContextMenu MenuItem", "native click Move to top",
                _lastClickDiagnostic, "order unchanged", MenuItemSummary(target));
        var state = await WaitUntilAsync(() => IsFirstPinned(target, fixture)
            && ProjectionOrder(target).FirstOrDefault() == fingerprint, timeoutSeconds).ConfigureAwait(false);
        var stateLines = await WaitForTelemetryAsync(target, lines => lines.Skip(telemetryStart).Any(line =>
            line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Movetotop target={fingerprint}", StringComparison.Ordinal))
            && lines.Skip(telemetryStart).Any(line => line.Contains($"APP_VIEW_STATE phase=after target={fingerprint}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        var newEvents = stateLines.Skip(telemetryStart).ToArray();
        var actualAction = newEvents.Any(line => line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Movetotop target={fingerprint}", StringComparison.Ordinal));
        var hasStatePair = TryGetViewStatePair(newEvents, fingerprint, out var beforeState, out var afterState);
        var preserved = hasStatePair && SameConversationData(beforeState, afterState, compareComposer: true);
        var activePreserved = hasStatePair && beforeState.ActiveFingerprint == afterState.ActiveFingerprint;
        var orderAfter = ProjectionOrder(target);
        var orderChanged = indexBefore > 0 && orderAfter.FirstOrDefault() == fingerprint
            && !orderBefore.SequenceEqual(orderAfter, StringComparer.Ordinal);
        report.Add("UI54-MOUSE-MOVE-TOP", "pinned Button", target.ProcessId, "right-click pinned row and select Move to top through native input",
            "native right-button and left-button transitions", $"target projection index moved from {indexBefore} to first={orderChanged}",
            $"Move to top WPF action observed={actualAction}; UIA first row matches={state}; target remains present={HasPinnedRow(target, fixture)}; transcript/unread/important/composer preserved={preserved}; active selection preserved={activePreserved}",
            watch.ElapsedMilliseconds, state && orderChanged && actualAction && preserved && activePreserved ? "PASS" : "FAIL", "Level 1 — Native OS input",
            $"order before={string.Join(',', orderBefore)}; after={string.Join(',', orderAfter)}; state pair={hasStatePair}; before={FormatConversationState(beforeState)}; after={FormatConversationState(afterState)}");
        return state && orderChanged && actualAction && preserved && activePreserved;
    }

    private static async Task<bool> UnpinMouseAsync(AcceptanceTarget target, AcceptanceReport report,
        FixtureConversation fixture, string fingerprint, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var row = FindPinnedButton(target, fixture);
        if (row is null) return AddBlocked(report, "UI54-MOUSE-UNPIN", "Pinned Button", "target pinned row not found");
        var menuResult = await OpenContextMenuAsync(target, row, "PinnedConversationContextMenu", timeoutSeconds).ConfigureAwait(false);
        if (!menuResult.Opened)
            return AddFail(report, "UI54-MOUSE-UNPIN", "Pinned Button", "open pinned row context menu", "context menu did not open", "pin remains", "menu action missing");
        var action = FindMenuAction(target, menuResult.Menu, "ContextAction.Unpinconversation");
        var telemetryStart = ReadTelemetry(target).Length;
        if (action is null || !await ClickElementAsync(target, action, false).ConfigureAwait(false))
            return AddFail(report, "UI54-MOUSE-UNPIN", "Pinned Button", "native click Unpin conversation", "action not delivered", "pin remains", "unpin action not invoked");
        var state = await WaitUntilAsync(() => !HasPinnedRow(target, fixture), timeoutSeconds).ConfigureAwait(false);
        var stateLines = await WaitForTelemetryAsync(target, lines => lines.Skip(telemetryStart).Any(line =>
            line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation target={fingerprint}", StringComparison.Ordinal))
            && lines.Skip(telemetryStart).Any(line => line.Contains($"APP_VIEW_STATE phase=after target={fingerprint}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        var newEvents = stateLines.Skip(telemetryStart).ToArray();
        var actualAction = newEvents.Any(line => line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation target={fingerprint}", StringComparison.Ordinal));
        var hasStatePair = TryGetViewStatePair(newEvents, fingerprint, out var beforeState, out var afterState);
        var preserved = hasStatePair && SameConversationData(beforeState, afterState, compareComposer: true);
        var activePreserved = hasStatePair && beforeState.ActiveFingerprint == afterState.ActiveFingerprint;
        report.Add("UI54-MOUSE-UNPIN", "pinned Button", target.ProcessId, "right-click pinned row and select Unpin via native input",
            "native right-button and left-button transitions", $"target removed; remaining pins={ProjectionOrder(target).Length}",
            $"WPF Unpin action target correlated={actualAction}; UIA target row removed={state}; transcript/unread/important/composer preserved={preserved}; active selection preserved={activePreserved}", watch.ElapsedMilliseconds,
            state && actualAction && preserved && activePreserved ? "PASS" : "FAIL", "Level 1 — Native OS input",
            $"state pair={hasStatePair}; before={FormatConversationState(beforeState)}; after={FormatConversationState(afterState)}");
        return state && actualAction && preserved && activePreserved;
    }

    private static async Task<bool> ActivatePinnedMouseAsync(AcceptanceTarget target, AcceptanceReport report,
        FixtureConversation fixture, string fingerprint, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var row = FindPinnedButton(target, fixture);
        if (row is null) return AddBlocked(report, "UI54-PINNED-DESTINATION", "Pinned Button", "destination row not found");
        var telemetryStart = ReadTelemetry(target).Length;
        var clicked = await ClickElementAsync(target, row, false).ConfigureAwait(false);
        var activationLines = clicked
            ? await WaitForTelemetryAsync(target, lines => lines.Skip(telemetryStart).Any(line =>
                line.Contains($"APP_PINNED_ACTIVATION target={fingerprint}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false)
            : [];
        var hasActivation = TryParsePinnedActivation(activationLines.Skip(telemetryStart), fingerprint, out var activated);
        var okay = hasActivation && activated.IsOpen && activated.ActiveFingerprint == fingerprint;
        report.Add("UI54-PINNED-DESTINATION", "pinned Button", target.ProcessId, "activate destination using native left-button input",
            "native mouse click; no UIA InvokePattern used", $"pinned activation event recorded={hasActivation}",
            $"target open={activated.IsOpen}; active selection matches destination={activated.ActiveFingerprint == fingerprint}; {FormatConversationState(activated)}",
            watch.ElapsedMilliseconds, okay ? "PASS" : "FAIL", "Level 1 — Native OS input");
        return okay;
    }

    private static async Task<bool> KeyboardJourneyAsync(AcceptanceTarget target, AcceptanceReport report,
        FixtureConversation alpha, FixtureConversation beta, string alphaHash, string betaHash, int timeoutSeconds)
    {
        var pinWatch = Stopwatch.StartNew();
        var alphaTreeRow = ExpandAndFindChannel(target, alpha.Network, alpha.Name);
        if (alphaTreeRow is null) return AddBlocked(report, "UI54-KEYBOARD-PIN-CONTEXT", "TreeItem", "unpinned channel row missing");
        var priorTreeRow = ExpandAndFindChannel(target, beta.Network, beta.Name);
        if (priorTreeRow is null) return AddBlocked(report, "UI54-KEYBOARD-NAVIGATION-TO-PIN", "TreeItem", "preceding channel row missing");
        var siblingRows = GetTreeItemSiblings(priorTreeRow);
        var priorRuntimeId = priorTreeRow.GetRuntimeId();
        var alphaRuntimeId = alphaTreeRow.GetRuntimeId();
        var priorIndex = Array.FindIndex(siblingRows, row => row.GetRuntimeId().SequenceEqual(priorRuntimeId));
        var alphaIndex = Array.FindIndex(siblingRows, row => row.GetRuntimeId().SequenceEqual(alphaRuntimeId));
        if (priorIndex < 0 || alphaIndex < 0)
            return AddBlocked(report, "UI54-KEYBOARD-NAVIGATION-TO-PIN", "TreeItem siblings",
                $"the expanded network did not expose both fixture rows under the same parent; sibling count={siblingRows.Length}");
        target.EnsureForeground();
        priorTreeRow.SetFocus();
        var priorFocus = FocusFingerprint();
        var navigationKey = alphaIndex < priorIndex ? NativeMethods.VirtualKeyUp : NativeMethods.VirtualKeyDown;
        var navigationName = alphaIndex < priorIndex ? "Up" : "Down";
        var navigationSteps = Math.Abs(alphaIndex - priorIndex);
        var navigationSent = 0;
        for (var index = 0; index < navigationSteps && !IsFocusedWithin(alphaRuntimeId); index++)
        {
            var pair = SendKeyPair(target, navigationKey, extended: true);
            navigationSent += pair.Sent;
            await Task.Delay(80).ConfigureAwait(false);
        }
        var navigatedToAlpha = IsFocusedWithin(alphaRuntimeId);
        report.Add("UI54-KEYBOARD-NAVIGATION-TO-PIN", "focused conversation TreeViewItem", target.ProcessId,
            $"navigate from AlphaNet #general to AlphaNet #alpha with {navigationName} arrows",
            $"SendInput {navigationSent}/{navigationSteps * 2}; focus changed={FocusFingerprint() != priorFocus}",
            $"native keyboard focus reached the eligible AlphaNet #alpha row={navigatedToAlpha}",
            $"sibling index={priorIndex}->{alphaIndex}; focused element fingerprint={FocusFingerprint()}", pinWatch.ElapsedMilliseconds,
            navigatedToAlpha && navigationSent == navigationSteps * 2 ? "PASS" : "FAIL", "Level 1 — Native OS input");
        if (!navigatedToAlpha)
            return false;
        var pinMenuStart = ReadTelemetry(target).Length;
        var pinMenuKey = SendKeyPair(target, NativeMethods.VirtualKeyApps, extended: true);
        var channelMenu = await WaitForMenuAsync(target, "ChannelContextMenu", timeoutSeconds, pinMenuStart).ConfigureAwait(false);
        var channelMenuOpened = channelMenu is not null;
        report.Add("UI54-KEYBOARD-PIN-CONTEXT", "focused channel TreeItem", target.ProcessId,
            "open the channel context menu with native Apps key",
            $"SendInput {pinMenuKey.Sent}/2; WPF menu event observed={channelMenuOpened}", "no action invoked",
            $"ChannelContextMenu UIA items={MenuItems(target, channelMenu).Length}", pinWatch.ElapsedMilliseconds,
            channelMenuOpened ? "PASS" : "FAIL", "Level 1 — Native OS input",
            $"focused element fingerprint={FocusFingerprint()}; {MenuDiagnostics(target, channelMenu)}; {SafeTail(ReadTelemetry(target), 8)}");
        if (channelMenu is null) return false;

        var pinItems = MenuItems(target, channelMenu);
        var initialMenuFocus = await WaitUntilAsync(() => IsFocusedOneOf(pinItems), timeoutSeconds).ConfigureAwait(false);
        report.Add("UI54-KEYBOARD-MENU-INITIAL-FOCUS", "ChannelContextMenu MenuItem", target.ProcessId,
            "inspect the initial focus after native Apps context-menu gesture", "no additional input sent",
            $"focused element is one of the visible menu items={initialMenuFocus}",
            $"menu item count={pinItems.Length}; focused element fingerprint={FocusFingerprint()}", pinWatch.ElapsedMilliseconds,
            initialMenuFocus ? "PASS" : "FAIL", "Level 2 — Runtime UI Automation");
        var pinIndex = Array.FindIndex(pinItems, item => CurrentName(item).Contains("Pin conversation", StringComparison.OrdinalIgnoreCase)
            && !CurrentName(item).Contains("Unpin", StringComparison.OrdinalIgnoreCase));
        if (pinIndex < 0) return AddBlocked(report, "UI54-KEYBOARD-PIN", "ChannelContextMenu", "Pin conversation action not available");
        SendKeyPair(target, NativeMethods.VirtualKeyHome, false);
        for (var index = 0; index < pinIndex; index++) SendKeyPair(target, NativeMethods.VirtualKeyDown, false);
        var pinStart = ReadTelemetry(target).Length;
        var pinEnter = SendKeyPair(target, NativeMethods.VirtualKeyReturn, false);
        var pinState = await WaitUntilAsync(() => HasPinnedRow(target, alpha), timeoutSeconds).ConfigureAwait(false);
        var pinLines = await WaitForTelemetryAsync(target, lines => lines.Skip(pinStart).Any(line =>
            line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Pinconversation target={alphaHash}", StringComparison.Ordinal))
            && lines.Skip(pinStart).Any(line => line.Contains($"APP_VIEW_STATE phase=after target={alphaHash}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        var pinEvents = pinLines.Skip(pinStart).ToArray();
        var pinAction = pinEvents.Any(line => line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Pinconversation target={alphaHash}", StringComparison.Ordinal));
        var pinProjection = ProjectionOrder(target).Contains(alphaHash, StringComparer.Ordinal);
        var pinHasPair = TryGetViewStatePair(pinEvents, alphaHash, out var pinBefore, out var pinAfter);
        var pinPreserved = pinHasPair && SameConversationData(pinBefore, pinAfter, compareComposer: true);
        var pinActivePreserved = pinHasPair && pinBefore.ActiveFingerprint == pinAfter.ActiveFingerprint;
        report.Add("UI54-KEYBOARD-PIN", "focused ChannelContextMenu MenuItem", target.ProcessId,
            "navigate to Pin conversation and press Enter using native keyboard input",
            $"Enter delivered={pinEnter.Sent == 2}; WPF action correlated={pinAction}",
            $"pinned UIA row={pinState}; projection contains target={pinProjection}; transcript/unread/important/composer preserved={pinPreserved}; active selection preserved={pinActivePreserved}",
            $"state pair={pinHasPair}; before={FormatConversationState(pinBefore)}; after={FormatConversationState(pinAfter)}",
            pinWatch.ElapsedMilliseconds, pinState && pinProjection && pinAction && pinPreserved && pinActivePreserved ? "PASS" : "FAIL", "Level 1 — Native OS input");
        if (!pinState || !pinProjection || !pinAction || !pinPreserved || !pinActivePreserved) return false;

        var activateWatch = Stopwatch.StartNew();
        var alphaPinned = FindPinnedButton(target, alpha);
        if (alphaPinned is null) return AddBlocked(report, "UI54-KEYBOARD-DESTINATION", "Pinned Button", "newly pinned destination missing");
        target.EnsureForeground();
        alphaPinned.SetFocus();
        var activationStart = ReadTelemetry(target).Length;
        var activationKey = SendKeyPair(target, NativeMethods.VirtualKeyReturn, false);
        var activationLines = await WaitForTelemetryAsync(target, lines => lines.Skip(activationStart).Any(line =>
            line.Contains($"APP_PINNED_ACTIVATION target={alphaHash}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        var activationAvailable = TryParsePinnedActivation(activationLines.Skip(activationStart), alphaHash, out var activatedState);
        var activation = activationAvailable && activatedState.IsOpen
            && activatedState.ActiveFingerprint == alphaHash
            && SameConversationState(pinAfter, activatedState, compareComposer: true);
        report.Add("UI54-KEYBOARD-DESTINATION", "focused pinned Button", target.ProcessId,
            "activate the keyboard-pinned destination with native Enter",
            $"SendInput {activationKey.Sent}/2; pinned activation event correlated={activationAvailable}",
            $"target open after activation={activatedState.IsOpen}; transcript/unread/important/draft and view identity preserved={activation}; active selection matches destination={activatedState.ActiveFingerprint == alphaHash}",
            $"composer bytes={activatedState.ComposerBytes}; composer fingerprint={activatedState.ComposerFingerprint}",
            activateWatch.ElapsedMilliseconds, activation ? "PASS" : "FAIL", "Level 1 — Native OS input",
            "Composer text is represented only by a short SHA-256 fingerprint.");
        if (!activation) return false;

        var moveTarget = report.IsAttached ? beta : alpha;
        var moveHash = report.IsAttached ? betaHash : alphaHash;
        var moveWatch = Stopwatch.StartNew();
        var moveRow = FindPinnedButton(target, moveTarget);
        if (moveRow is null) return AddBlocked(report, "UI54-KEYBOARD-CONTEXT", "Pinned Button", "keyboard move target missing");
        moveRow.SetFocus();
        var moveMenuStart = ReadTelemetry(target).Length;
        var moveMenuKey = SendKeyPair(target, NativeMethods.VirtualKeyApps, extended: true);
        var moveMenu = await WaitForMenuAsync(target, "PinnedConversationContextMenu", timeoutSeconds, moveMenuStart).ConfigureAwait(false);
        var moveMenuOpened = moveMenu is not null;
        report.Add("UI54-KEYBOARD-CONTEXT", "focused pinned Button", target.ProcessId,
            "open a pinned conversation context menu with native Apps key",
            $"SendInput {moveMenuKey.Sent}/2; WPF menu event observed={moveMenuOpened}", "no action invoked",
            $"PinnedConversationContextMenu UIA items={MenuItems(target, moveMenu).Length}", moveWatch.ElapsedMilliseconds,
            moveMenuOpened ? "PASS" : "FAIL", "Level 1 — Native OS input");
        if (moveMenu is null) return false;

        var moveItems = MenuItems(target, moveMenu);
        var moveIndex = Array.FindIndex(moveItems, item => CurrentName(item).Contains("Move to top", StringComparison.OrdinalIgnoreCase));
        if (moveIndex < 0) return AddBlocked(report, "UI54-KEYBOARD-MOVE-TOP", "PinnedConversationContextMenu", "Move to top menu item not available");
        SendKeyPair(target, NativeMethods.VirtualKeyHome, false);
        for (var index = 0; index < moveIndex; index++) SendKeyPair(target, NativeMethods.VirtualKeyDown, false);
        var moveBeforeProjection = ProjectionOrder(target);
        var moveBeforeIndex = Array.IndexOf(moveBeforeProjection, moveHash);
        var moveStart = ReadTelemetry(target).Length;
        var moveEnter = SendKeyPair(target, NativeMethods.VirtualKeyReturn, false);
        var moved = await WaitUntilAsync(() => IsFirstPinned(target, moveTarget)
            && ProjectionOrder(target).FirstOrDefault() == moveHash, timeoutSeconds).ConfigureAwait(false);
        var moveLines = await WaitForTelemetryAsync(target, lines => lines.Skip(moveStart).Any(line =>
            line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Movetotop target={moveHash}", StringComparison.Ordinal))
            && lines.Skip(moveStart).Any(line => line.Contains($"APP_VIEW_STATE phase=after target={moveHash}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        var moveEvents = moveLines.Skip(moveStart).ToArray();
        var moveAction = moveEvents.Any(line => line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Movetotop target={moveHash}", StringComparison.Ordinal));
        var moveHasPair = TryGetViewStatePair(moveEvents, moveHash, out var moveBefore, out var moveAfter);
        var movePreserved = moveHasPair && SameConversationData(moveBefore, moveAfter, compareComposer: true);
        var moveActivePreserved = moveHasPair && moveBefore.ActiveFingerprint == moveAfter.ActiveFingerprint;
        var moveAfterProjection = ProjectionOrder(target);
        var moveOrderChanged = moveBeforeIndex > 0 && moveAfterProjection.FirstOrDefault() == moveHash
            && !moveBeforeProjection.SequenceEqual(moveAfterProjection, StringComparer.Ordinal);
        var movePassed = moved && moveOrderChanged && moveAction && movePreserved && moveActivePreserved && moveEnter.Sent == 2;
        report.Add("UI54-KEYBOARD-MOVE-TOP", "focused PinnedConversationContextMenu MenuItem", target.ProcessId,
            "navigate to Move to top and press Enter using native keyboard input",
            $"Enter delivered={moveEnter.Sent == 2}; WPF action correlated={moveAction}",
            $"target row moved from projection index {moveBeforeIndex} to first={moveOrderChanged}; UIA first row matches={moved}; transcript/unread/important/composer preserved={movePreserved}; active selection preserved={moveActivePreserved}",
            $"target={moveHash}; order before={string.Join(',', moveBeforeProjection)}; after={string.Join(',', moveAfterProjection)}; state pair={moveHasPair}; before={FormatConversationState(moveBefore)}; after={FormatConversationState(moveAfter)}",
            moveWatch.ElapsedMilliseconds, movePassed ? "PASS" : "FAIL", "Level 1 — Native OS input");
        if (!movePassed) return false;

        var unpinWatch = Stopwatch.StartNew();
        var betaRow = FindPinnedButton(target, beta);
        if (betaRow is null) return AddBlocked(report, "UI54-KEYBOARD-UNPIN", "Pinned Button", "AlphaNet #general pinned row missing after move");
        betaRow.SetFocus();
        var unpinMenuStart = ReadTelemetry(target).Length;
        var unpinMenuKey = SendKeyPair(target, NativeMethods.VirtualKeyApps, extended: true);
        var unpinMenu = await WaitForMenuAsync(target, "PinnedConversationContextMenu", timeoutSeconds, unpinMenuStart).ConfigureAwait(false);
        if (unpinMenu is null)
            return AddFail(report, "UI54-KEYBOARD-UNPIN", "focused pinned Button", "open context menu with native Apps key",
                $"SendInput {unpinMenuKey.Sent}/2", "pin unchanged", "WPF pinned menu absent");
        var unpinItems = MenuItems(target, unpinMenu);
        var unpinIndex = Array.FindIndex(unpinItems, item => CurrentName(item).Contains("Unpin conversation", StringComparison.OrdinalIgnoreCase));
        if (unpinIndex < 0) return AddBlocked(report, "UI54-KEYBOARD-UNPIN", "PinnedConversationContextMenu", "Unpin menu item not available");
        SendKeyPair(target, NativeMethods.VirtualKeyHome, false);
        for (var index = 0; index < unpinIndex; index++) SendKeyPair(target, NativeMethods.VirtualKeyDown, false);
        var unpinStart = ReadTelemetry(target).Length;
        var unpinEnter = SendKeyPair(target, NativeMethods.VirtualKeyReturn, false);
        var unpinned = await WaitUntilAsync(() => !HasPinnedRow(target, beta) && HasPinnedRow(target, alpha), timeoutSeconds).ConfigureAwait(false);
        var unpinLines = await WaitForTelemetryAsync(target, lines => lines.Skip(unpinStart).Any(line =>
            line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation target={betaHash}", StringComparison.Ordinal))
            && lines.Skip(unpinStart).Any(line => line.Contains($"APP_VIEW_STATE phase=after target={betaHash}", StringComparison.Ordinal)), timeoutSeconds).ConfigureAwait(false);
        var unpinEvents = unpinLines.Skip(unpinStart).ToArray();
        var unpinAction = unpinEvents.Any(line => line.Contains($"APP_CONTEXT_MENU_ACTION id=ContextAction.Unpinconversation target={betaHash}", StringComparison.Ordinal));
        var unpinHasPair = TryGetViewStatePair(unpinEvents, betaHash, out var unpinBefore, out var unpinAfter);
        var unpinPreserved = unpinHasPair && SameConversationData(unpinBefore, unpinAfter, compareComposer: true);
        var unpinActivePreserved = unpinHasPair && unpinBefore.ActiveFingerprint == unpinAfter.ActiveFingerprint;
        var unpinPassed = unpinned && unpinAction && unpinPreserved && unpinActivePreserved && unpinEnter.Sent == 2;
        report.Add("UI54-KEYBOARD-UNPIN", "focused PinnedConversationContextMenu MenuItem", target.ProcessId,
            "navigate to Unpin conversation and press Enter using native keyboard input",
            $"Enter delivered={unpinEnter.Sent == 2}; WPF action correlated={unpinAction}",
            $"AlphaNet #general row removed and #alpha retained={unpinned}; transcript/unread/important/composer preserved={unpinPreserved}; active selection preserved={unpinActivePreserved}",
            $"state pair={unpinHasPair}; before={FormatConversationState(unpinBefore)}; after={FormatConversationState(unpinAfter)}",
            unpinWatch.ElapsedMilliseconds, unpinPassed ? "PASS" : "FAIL", "Level 1 — Native OS input");
        report.Add("UI54-KEYBOARD-ACTIONS", "pinned conversation ContextMenu", target.ProcessId,
            "complete keyboard Move to top and Unpin actions", $"Move to top={movePassed}; Unpin={unpinPassed}",
            $"pinned #general row removed={unpinned}; #alpha row remains={HasPinnedRow(target, alpha)}",
            "both menu action telemetry records are correlated to their target fingerprints",
            moveWatch.ElapsedMilliseconds + unpinWatch.ElapsedMilliseconds,
            movePassed && unpinPassed ? "PASS" : "FAIL", "Level 1 — Native OS input");

        return unpinPassed;
    }

    private static async Task<bool> KeyboardNavigateAsync(AcceptanceTarget target, AcceptanceReport report, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var pinList = FindByAutomationId(target, "PinnedConversations");
        var rows = GetPinnedButtons(target, pinList).ToArray();
        if (pinList is null || rows.Length < 2) return AddBlocked(report, "UI54-KEYBOARD-NAVIGATION", "Pinned conversations List",
            $"populated pinned list unavailable; {PinnedListSummary(target)}");
        target.EnsureForeground();
        pinList.SetFocus();
        var before = FocusFingerprint();
        var telemetryStart = ReadTelemetry(target).Length;
        var input = SendKeyPair(target, NativeMethods.VirtualKeyDown, false);
        var changed = await WaitUntilAsync(() => FocusFingerprint() != before, timeoutSeconds).ConfigureAwait(false);
        var after = FocusFingerprint();
        var wpfKey = ReadTelemetry(target).Skip(telemetryStart).Any(static line => line == "WPF_PREVIEW_KEY_DOWN");
        var focused = FocusedAutomationElement();
        var withinPinnedList = focused is not null && IsWithinAutomationId(focused, "PinnedConversations");
        report.Add("UI54-KEYBOARD-NAVIGATION", "populated Pinned conversations List", target.ProcessId, "press native Down arrow from the focused pinned list",
            $"SendInput {input.Sent}/2; WPF key event observed={wpfKey}",
            $"focus changed from {before} to {after}={changed}; focus remains in the pinned list={withinPinnedList}; row count={rows.Length}",
            $"focused element fingerprint={after}", watch.ElapsedMilliseconds,
            changed && wpfKey && withinPinnedList && input.Sent == 2 ? "PASS" : "FAIL", "Level 1 — Native OS input");
        return changed && wpfKey && withinPinnedList && input.Sent == 2;
    }

    private static bool IsFocusedWithin(int[] targetRuntimeId)
    {
        var focused = FocusedAutomationElement();
        if (focused is null) return false;
        try
        {
            if (focused.GetRuntimeId().SequenceEqual(targetRuntimeId)) return true;
            return GetAncestors(focused, 8).Any(ancestor => ancestor.GetRuntimeId().SequenceEqual(targetRuntimeId));
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static bool IsWithinAutomationId(AutomationElement element, string automationId)
    {
        try
        {
            if (string.Equals(element.Current.AutomationId, automationId, StringComparison.Ordinal)) return true;
            return GetAncestors(element, 8).Any(ancestor => string.Equals(ancestor.Current.AutomationId, automationId, StringComparison.Ordinal));
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static bool IsFocusedOneOf(IEnumerable<AutomationElement> candidates)
    {
        var focused = FocusedAutomationElement();
        if (focused is null) return false;
        try
        {
            var focusedRuntimeId = focused.GetRuntimeId();
            return candidates.Any(candidate => candidate.GetRuntimeId().SequenceEqual(focusedRuntimeId));
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static async Task<bool> CloseReopenAsync(AcceptanceTarget target, AcceptanceReport report,
        FixtureConversation fixture, string fingerprint, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var targetRow = await WaitElementAsync(() => FindPinnedButton(target, fixture), timeoutSeconds).ConfigureAwait(false);
        if (targetRow is null) return AddBlocked(report, "UI54-PIN-CLOSE-REOPEN", "Pinned Button",
            "pinned conversation not found; " + PinnedListSummary(target));
        var menuResult = await OpenContextMenuAsync(target, targetRow, "PinnedConversationContextMenu", timeoutSeconds).ConfigureAwait(false);
        if (!menuResult.Opened)
            return AddFail(report, "UI54-PIN-CLOSE-REOPEN", "Pinned Button", "open pinned conversation context menu", "menu did not open", "view remains open", "no UI action");
        var closeAction = FindMenuAction(target, menuResult.Menu, "ContextAction.CloseviewwithoutPART");
        var closeStart = ReadTelemetry(target).Length;
        if (closeAction is null || !await ClickElementAsync(target, closeAction, false).ConfigureAwait(false))
            return AddFail(report, "UI54-PIN-CLOSE-REOPEN", "Pinned conversation context menu", "select Close view without PART", "native click failed", "view remains open", MenuItemSummary(target));
        var closeLines = await WaitForTelemetryAsync(target, lines => lines.Skip(closeStart).Any(line =>
            TryParseViewState(line, "after", fingerprint, out _)), timeoutSeconds).ConfigureAwait(false);
        var closeEvents = closeLines.Skip(closeStart).ToArray();
        var closeActionObserved = closeEvents.Any(line => line.Contains(
            $"APP_CONTEXT_MENU_ACTION id=ContextAction.CloseviewwithoutPART target={fingerprint}", StringComparison.Ordinal));
        var closeHasPair = TryGetViewStatePair(closeEvents, fingerprint, out var beforeClose, out var afterClose);
        var rawClosedEvent = closeEvents.Any(line => line.Contains($"APP_VIEW_STATE phase=after target={fingerprint} open=false", StringComparison.Ordinal));
        var closed = closeHasPair && !afterClose.IsOpen;
        var closeStatePreserved = closeHasPair && beforeClose.IsOpen && closed
            && SameConversationState(beforeClose, afterClose, compareComposer: true, compareActive: false)
            && beforeClose.ActiveFingerprint == afterClose.ActiveFingerprint;
        var pinRemains = HasPinnedRow(target, fixture);
        if (!closed || !pinRemains || !closeActionObserved || !closeStatePreserved)
            return AddFail(report, "UI54-PIN-CLOSE-REOPEN", "Channel context menu", "close view and retain pin",
                $"closed={closed}; raw close event={rawClosedEvent}; pin retained={pinRemains}; action correlated={closeActionObserved}; transcript/unread/important preserved={closeStatePreserved}",
                "pin retained=" + pinRemains, $"view close state was not fully established; state pair={closeHasPair}; before open={beforeClose.IsOpen}; after open={afterClose.IsOpen}");
        var pinned = FindPinnedButton(target, fixture);
        var reopenStart = ReadTelemetry(target).Length;
        var clicked = pinned is not null && await ClickElementAsync(target, pinned, false).ConfigureAwait(false);
        var reopenLines = clicked
            ? await WaitForTelemetryAsync(target, lines => lines.Skip(reopenStart).Any(line =>
                IsPinnedReopenEvent(line, fingerprint)), timeoutSeconds).ConfigureAwait(false)
            : [];
        var reopenEvents = reopenLines.Skip(reopenStart).ToArray();
        var activated = reopenEvents.Any(line => IsPinnedReopenEvent(line, fingerprint));
        var reopenStateAvailable = TryParsePinnedActivation(reopenEvents, fingerprint, out var reopenedState);
        var restored = reopenStateAvailable && reopenedState.IsOpen && reopenedState.ActiveFingerprint == fingerprint
            && SameConversationData(beforeClose, reopenedState, compareComposer: true);
        var rows = FindChannelRows(target, fixture.Network, fixture.Name).Length;
        var noDuplicate = rows <= 1 && CountPinnedRows(target, fixture) == 1;
        report.Add("UI54-PIN-CLOSE-REOPEN", "Pinned Button and PinnedConversationContextMenu", target.ProcessId,
            "close channel view through its pinned context action then activate its pinned destination",
            "native right-click, native menu selection, and native left-click activation",
            $"view closed={closed}; pin retained={pinRemains}; reopen event={activated}; transcript/unread/important/composer restored={restored}; duplicate TreeItems absent={noDuplicate}",
            $"UIA TreeItem count={rows}; unique pinned row={CountPinnedRows(target, fixture) == 1}; close pair={closeHasPair}; before close={FormatConversationState(beforeClose)}; after close={FormatConversationState(afterClose)}; reopened={FormatConversationState(reopenedState)}; reopen activation telemetry={SafeTail(reopenEvents.Where(static line => line.StartsWith("APP_PINNED_ACTIVATION ", StringComparison.Ordinal)).ToArray(), 3)}",
            watch.ElapsedMilliseconds, closed && pinRemains && closeActionObserved && closeStatePreserved && activated && restored && noDuplicate ? "PASS" : "FAIL",
            "Level 1 — Native OS input", "Composer text is represented only by a short SHA-256 fingerprint.");
        return closed && pinRemains && closeActionObserved && closeStatePreserved && activated && restored && noDuplicate;
    }

    private static async Task ManualFocusCheckpointAsync(AcceptanceTarget target, AcceptanceReport report, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        Console.WriteLine("\nUI54-KEYBOARD-NAVIGATION");
        Console.WriteLine("Focus the conversation TreeView, navigate between channel rows using the arrow keys, then press Enter here.");
        var start = ReadTelemetry(target).Length;
        await ReadOperatorCheckpointAsync().ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string[] lines;
        do
        {
            lines = ReadTelemetry(target).ToArray();
            if (HasRecordPair(lines.Skip(start), "WPF_PREVIEW_KEY_DOWN", "WPF_PREVIEW_KEY_UP")) break;
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(150).ConfigureAwait(false);
        } while (true);
        var newEvents = lines.Skip(start).ToArray();
        var keyboard = HasRecordPair(newEvents, "WPF_PREVIEW_KEY_DOWN", "WPF_PREVIEW_KEY_UP");
        var focused = FocusedAutomationElement();
        var focusInTarget = focused is not null && focused.Current.ProcessId == target.ProcessId;
        var focusClass = ControlClass(focused);
        report.Add("UI54-KEYBOARD-NAVIGATION", "conversation TreeView", target.ProcessId, "operator navigates conversation list by keyboard",
            $"WPF key down/up pair observed={keyboard}", $"focus remains in target process={focusInTarget}",
            $"focused control={focusClass}; focused name fingerprint={FocusFingerprint()}", watch.ElapsedMilliseconds,
            keyboard && focusInTarget ? "PASS" : "NOT RUN", "Level 1 — Manual witnessed OS input",
            "Windows does not expose physical-versus-UIA origin here; the runner did not inject input during manual mode.");
    }

    private static async Task ManualCloseReopenCheckpointAsync(AcceptanceTarget target, AcceptanceReport report,
        FixtureConversation fixture, string fingerprint, int timeoutSeconds)
    {
        Console.WriteLine("\nUI54-PIN-CLOSE-REOPEN");
        Console.WriteLine("With AlphaNet #general active, right-click AlphaNet #alpha in Pinned and choose Close view without PART. Then activate AlphaNet #alpha from Pinned and press Enter here.");
        Console.WriteLine("The runner checks the close action, retained pin, reopened view, stable view identity, transcript/unread/important state, composer fingerprint, and duplicate rows.");
        var watch = Stopwatch.StartNew();
        var start = ReadTelemetry(target).Length;
        await ReadOperatorCheckpointAsync().ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string[] lines;
        while (true)
        {
            lines = ReadTelemetry(target).ToArray();
            var closed = lines.Skip(start).Any(line => line.Contains($"APP_VIEW_STATE phase=after target={fingerprint} open=false", StringComparison.Ordinal));
            var reopenedEvent = lines.Skip(start).Any(line => IsPinnedReopenEvent(line, fingerprint));
            if (closed && reopenedEvent) break;
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(150).ConfigureAwait(false);
        }
        var events = lines.Skip(start).ToArray();
        var closeAction = events.Any(line => line.Contains(
            $"APP_CONTEXT_MENU_ACTION id=ContextAction.CloseviewwithoutPART target={fingerprint}", StringComparison.Ordinal));
        var closePair = TryGetViewStatePair(events, fingerprint, out var beforeClose, out var afterClose);
        var closeState = closePair && beforeClose.IsOpen && !afterClose.IsOpen
            && SameConversationState(beforeClose, afterClose, compareComposer: true, compareActive: false)
            && beforeClose.ActiveFingerprint == afterClose.ActiveFingerprint;
        var reopened = TryParsePinnedActivation(events, fingerprint, out var reopenedState) && reopenedState.IsOpen;
        var restored = reopened && reopenedState.ActiveFingerprint == fingerprint
            && SameConversationData(beforeClose, reopenedState, compareComposer: true);
        var pinRetained = HasPinnedRow(target, fixture);
        var rows = FindChannelRows(target, fixture.Network, fixture.Name).Length;
        var uniqueView = rows <= 1 && CountPinnedRows(target, fixture) == 1
            && (!reopened || beforeClose.ViewFingerprint == reopenedState.ViewFingerprint);
        var wpfInput = events.Any(static line => line.StartsWith("WPF_PREVIEW_", StringComparison.Ordinal)
            || line.StartsWith("WPF_CONTEXT_MENU", StringComparison.Ordinal));
        var passed = closeAction && closeState && reopened && restored && pinRetained && uniqueView && wpfInput;
        report.Add("UI54-PIN-CLOSE-REOPEN", "pinned Button and PinnedConversationContextMenu", target.ProcessId,
            "operator closes the view through the pinned context menu, then activates the pinned row",
            $"real WPF input observed={wpfInput}; close action correlated={closeAction}; pinned activation observed={reopened}",
            $"closed={closeState}; pin retained={pinRetained}; transcript/unread/important/composer restored={restored}; duplicate view absent={uniqueView}",
            $"before close={FormatConversationState(beforeClose)}; after close={FormatConversationState(afterClose)}; reopened={FormatConversationState(reopenedState)}; UIA TreeItem count={rows}; unique pinned row={CountPinnedRows(target, fixture) == 1}",
            watch.ElapsedMilliseconds, passed ? "PASS" : "NOT RUN", "Level 1 — Manual witnessed OS input",
            "Windows does not expose physical-versus-UIA input source; the runner sent no input in manual mode.");
    }

    private static void RecordManualAccessibility(AcceptanceTarget target, AcceptanceReport report,
        string alphaFingerprint, string betaFingerprint)
    {
        var list = FindByAutomationId(target, "PinnedConversations");
        var rows = GetPinnedButtons(target, list).ToArray();
        var alphaFixture = new FixtureConversation("AlphaNet", "#alpha");
        var betaFixture = new FixtureConversation("AlphaNet", "#general");
        var unique = CountPinnedRows(rows, alphaFixture) == 1 && CountPinnedRows(rows, betaFixture) == 0;
        var accessible = list is not null && string.Equals(CurrentName(list), "Pinned conversations", StringComparison.Ordinal)
            && rows.All(static row => row.Current.IsEnabled && row.Current.IsKeyboardFocusable && !row.Current.IsOffscreen);
        var projection = ProjectionOrder(target);
        report.Add("UI54-UIA-ACCESSIBILITY", "populated Pinned List and Button rows", target.ProcessId,
            "inspect runtime names, focusability, visibility, membership, and duplicates",
            "no input sent", $"row count={rows.Length}; Alpha unique={CountPinnedRows(rows, alphaFixture) == 1}; Beta absent={CountPinnedRows(rows, betaFixture) == 0}",
            $"list name and row accessibility valid={accessible}; projection={string.Join(',', projection)}", 0,
            unique && accessible && projection.Contains(alphaFingerprint, StringComparer.Ordinal)
                && !projection.Contains(betaFingerprint, StringComparer.Ordinal) ? "PASS" : "FAIL",
            "Level 2 — Runtime UI Automation", PinnedListSummary(target));
    }

    private static async Task ManualRemoteFocusAsync(AcceptanceTarget target, AcceptanceReport report,
        string scenario, string instruction, int timeoutSeconds)
    {
        Console.WriteLine($"\n{scenario} — capture focus baseline");
        Console.WriteLine("In client A, focus the exact pinned row that client B will reorder or remove. Press Enter here to capture its UIA identity and the current order.");
        await ReadOperatorCheckpointAsync().ConfigureAwait(false);
        var beforeProjection = ProjectionOrder(target).ToArray();
        var beforeFocus = FocusedAutomationElement();
        var beforeWasPinned = IsCurrentPinnedButton(target, beforeFocus);
        Console.WriteLine($"\n{scenario}: {instruction}");
        Console.WriteLine("After the second-client update arrives, use the keyboard once in client A and press Enter here.");
        var watch = Stopwatch.StartNew();
        var beforeLines = ReadTelemetry(target).Length;
        await ReadOperatorCheckpointAsync().ConfigureAwait(false);
        var timeout = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < timeout)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var current = ProjectionOrder(target);
            if (!current.SequenceEqual(beforeProjection, StringComparer.Ordinal)) break;
            await Task.Delay(150).ConfigureAwait(false);
        }
        var afterProjection = ProjectionOrder(target);
        var changed = !afterProjection.SequenceEqual(beforeProjection, StringComparer.Ordinal);
        var focusAfter = FocusedAutomationElement();
        var focusExists = focusAfter is not null && focusAfter.Current.ProcessId == target.ProcessId;
        var afterWasPinned = IsCurrentPinnedButton(target, focusAfter);
        var focusAssertion = scenario.EndsWith("REMOVE", StringComparison.Ordinal)
            ? beforeWasPinned && afterWasPinned && beforeProjection.Except(afterProjection, StringComparer.Ordinal).Any()
                && !SameAutomationElement(beforeFocus, focusAfter)
            : beforeWasPinned && afterWasPinned
                && SameAutomationElement(beforeFocus, focusAfter);
        var lines = ReadTelemetry(target).Skip(beforeLines).ToArray();
        var keyboard = HasRecordPair(lines, "WPF_PREVIEW_KEY_DOWN", "WPF_PREVIEW_KEY_UP");
        var passed = changed && focusExists && focusAssertion && keyboard;
        report.Add(scenario, "pinned List and focused WPF window", target.ProcessId, "operator applies remote update from second authenticated client",
            $"projection changed={changed}; focus belonged to pinned item before update={beforeWasPinned}; focus is a remaining pinned item afterward={afterWasPinned}; navigation key pair={keyboard}",
            scenario.EndsWith("REMOVE", StringComparison.Ordinal) ? "focused item removed from projection" : "order projection changed",
            $"focus before={Fingerprint(CurrentName(beforeFocus))}; focus after={Fingerprint(CurrentName(focusAfter))}; focus transition valid={focusAssertion}", watch.ElapsedMilliseconds,
            passed ? "PASS" : "NOT RUN", "Level 1 — Manual witnessed OS input",
            passed ? "Remote projection change, client-side WPF focus, and post-update keyboard input were observed. Input source cannot be distinguished from UIA by Windows telemetry." : "No matching remote projection, focused-row transition, or post-update keyboard input was independently established.");
    }

    private static async Task ReadOperatorCheckpointAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_operatorCheckpointTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, timeout.Token);
        try
        {
            var response = await Console.In.ReadLineAsync(linked.Token).ConfigureAwait(false);
            if (response is null)
                throw new EndOfStreamException("The operator console closed before this checkpoint was acknowledged.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No operator response arrived within {_operatorCheckpointTimeoutSeconds} seconds; the remaining acceptance steps were not run.");
        }
    }

    private static bool IsCurrentPinnedButton(AcceptanceTarget target, AutomationElement? focused)
    {
        try
        {
            if (focused is null || focused.Current.ControlType != ControlType.Button) return false;
            var list = FindByAutomationId(target, "PinnedConversations");
            var focusRuntimeId = focused.GetRuntimeId();
            return GetPinnedButtons(target, list).Any(row => row.GetRuntimeId().SequenceEqual(focusRuntimeId));
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static bool SameAutomationElement(AutomationElement? left, AutomationElement? right)
    {
        try { return left is not null && right is not null && left.GetRuntimeId().SequenceEqual(right.GetRuntimeId()); }
        catch (ElementNotAvailableException) { return false; }
    }

    private static async Task ManualCheckpointAsync(AcceptanceTarget target, AcceptanceReport report,
        string scenario, string instruction, string expectedAction, string expectedTargetHash,
        Func<bool> stateAssertion, int timeoutSeconds, bool requirePreservation = false)
    {
        Console.WriteLine($"\n{scenario}: {instruction}");
        Console.WriteLine("Perform the action in nexIRC. Press Enter when finished; the runner still requires routed WPF and UIA/state evidence.");
        var watch = Stopwatch.StartNew();
        var start = ReadTelemetry(target).Length;
        await ReadOperatorCheckpointAsync().ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string[] lines;
        while (true)
        {
            lines = ReadTelemetry(target).ToArray();
            var expected = lines.Skip(start).Any(line => line.Contains(expectedAction, StringComparison.Ordinal)
                && (expectedTargetHash.Length == 0 || line.Contains(expectedTargetHash, StringComparison.Ordinal)));
            if (expected && stateAssertion()) break;
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(150).ConfigureAwait(false);
        }
        var actionSeen = lines.Skip(start).Any(line => line.Contains(expectedAction, StringComparison.Ordinal)
            && (expectedTargetHash.Length == 0 || line.Contains(expectedTargetHash, StringComparison.Ordinal)));
        var stateSeen = stateAssertion();
        var newEvents = lines.Skip(start).ToArray();
        var wpfInput = newEvents.Any(static line => line.StartsWith("WPF_PREVIEW_", StringComparison.Ordinal)
            || line.StartsWith("WPF_CONTEXT_MENU", StringComparison.Ordinal)
            || line.StartsWith("APP_CONTEXT_MENU_OPENED", StringComparison.Ordinal)
            || line.StartsWith("APP_CONTEXT_MENU_ACTION", StringComparison.Ordinal)
            || line.StartsWith("APP_PINNED_ACTIVATION", StringComparison.Ordinal));
        var before = default(ViewStateSnapshot);
        var after = default(ViewStateSnapshot);
        var hasStatePair = expectedTargetHash.Length == 12
            && TryGetViewStatePair(newEvents, expectedTargetHash, out before, out after);
        var preserved = hasStatePair && SameConversationData(before, after, compareComposer: true);
        var activePreserved = hasStatePair && before.ActiveFingerprint == after.ActiveFingerprint;
        var evidenceComplete = actionSeen && stateSeen && wpfInput
            && (!requirePreservation || preserved && activePreserved);
        var result = evidenceComplete ? "PASS"
            : actionSeen && stateSeen && wpfInput && requirePreservation && (!preserved || !activePreserved) ? "FAIL" : "NOT RUN";
        report.Add(scenario, "WPF routed conversation row/pinned Button", target.ProcessId, instruction,
            $"actual WPF input evidence={wpfInput}; action observed={actionSeen}",
            $"assertion satisfied={stateSeen}; transcript/unread/important/composer preserved={(!requirePreservation || preserved)}; active selection preserved={(!requirePreservation || activePreserved)}",
            $"target correlation fingerprint={(expectedTargetHash.Length == 0 ? "not applicable" : expectedTargetHash)}; state pair={hasStatePair}; before={FormatConversationState(before)}; after={FormatConversationState(after)}",
            watch.ElapsedMilliseconds, result, "Level 1 — Manual witnessed OS input",
            actionSeen && stateSeen && wpfInput
                ? "The manual checkpoint had a matching action and state/UI observation. Windows does not expose physical-versus-UIA input source."
                : "Enter did not count as evidence; no matching action/state/routed input was observed.");
    }

    private static async Task<ContextMenuOpenResult> OpenContextMenuAsync(AcceptanceTarget target, AutomationElement element,
        string expectedMenuId, int timeoutSeconds)
    {
        if (!await EnsureElementVisibleAsync(element, timeoutSeconds).ConfigureAwait(false))
            return new ContextMenuOpenResult(false, "target row could not be brought into the visible viewport");
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || !element.Current.IsEnabled || element.Current.IsOffscreen)
            return new ContextMenuOpenResult(false, $"target bounds empty={bounds.IsEmpty}; enabled={element.Current.IsEnabled}; offscreen={element.Current.IsOffscreen}");
        target.EnsureForeground();
        var point = ClickPoint(element);
        if (!Contains(bounds, point.X, point.Y))
            return new ContextMenuOpenResult(false, $"UIA-selected point is outside target bounds {RectText(bounds)}; no input sent");
        var pointOwner = target.GetPointOwnerPid(point.X, point.Y);
        if (pointOwner != target.ProcessId)
            return new ContextMenuOpenResult(false, $"WindowFromPoint owner PID={pointOwner}; target PID={target.ProcessId}; no input sent");
        var before = ReadTelemetry(target).Length;
        var sent = SendMousePair(target, point.X, point.Y, rightButton: true);
        if (sent.Sent != 2)
            return new ContextMenuOpenResult(false, $"SendInput sent {sent.Sent}/2; Win32 error={sent.Win32Error}; {sent.Description}");
        var menu = await WaitForMenuAsync(target, expectedMenuId, timeoutSeconds, before).ConfigureAwait(false);
        var opened = menu is not null;
        return new ContextMenuOpenResult(opened, opened ? "WPF context menu opened" : "native right-click delivered but WPF context menu was not observed", menu);
    }

    private static async Task<AutomationElement?> WaitForMenuAsync(AcceptanceTarget target, string expectedMenuId,
        int timeoutSeconds, int startIndex = 0)
    {
        return await WaitElementAsync(() =>
        {
            var open = ReadTelemetry(target).Skip(startIndex).Any(line => line == $"APP_CONTEXT_MENU_OPENED id={expectedMenuId}");
            if (!open) return null;
            foreach (var element in ElementsForProcess(target))
            {
                try
                {
                    if (element.Current.ControlType == ControlType.Menu
                        && string.Equals(element.Current.AutomationId, expectedMenuId, StringComparison.Ordinal)
                        && !element.Current.IsOffscreen)
                        return element;
                }
                catch (ElementNotAvailableException) { }
            }
            return null;
        }, timeoutSeconds).ConfigureAwait(false);
    }

    private static AutomationElement? FindMenuAction(AcceptanceTarget target, AutomationElement? menu, string automationId)
    {
        if (menu is null || menu.Current.IsOffscreen) return null;
        var candidates = ElementsForProcess(target).Where(element => element.Current.ControlType == ControlType.MenuItem
            && string.Equals(element.Current.AutomationId, automationId, StringComparison.Ordinal)
            && element.Current.IsEnabled && !element.Current.IsOffscreen);
        foreach (var candidate in candidates)
        {
            var point = ClickPoint(candidate);
            if (!Contains(candidate.Current.BoundingRectangle, point.X, point.Y)) continue;
            if (!target.OwnsPoint(point.X, point.Y)) continue;
            try
            {
                var hit = AutomationElement.FromPoint(new Point(point.X, point.Y));
                if (IsMenuAction(hit, automationId) || GetAncestors(hit, 5).Any(parent => IsMenuAction(parent, automationId)))
                    return candidate;
            }
            catch (ElementNotAvailableException) { }
        }
        return null;
    }

    private static bool IsMenuAction(AutomationElement element, string automationId) =>
        element.Current.ControlType == ControlType.MenuItem
        && string.Equals(element.Current.AutomationId, automationId, StringComparison.Ordinal);

    private static AutomationElement[] MenuItems(AcceptanceTarget target, AutomationElement? menu)
    {
        if (menu is null) return [];
        var descendants = menu.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
            .Cast<AutomationElement>()
            .Where(static element => element.Current.IsEnabled && !element.Current.IsOffscreen)
            .Take(MaxUiElements).ToArray();
        if (descendants.Length > 0) return descendants;

        // WPF ContextMenu is hosted in a separate popup HWND; UIA can expose the Menu
        // node without exposing its items as descendants. Recover visible menu items
        // from every top-level window owned by the validated target PID, scoped by
        // the popup's actual runtime bounds.
        var menuBounds = menu.Current.BoundingRectangle;
        var visibleItems = ElementsForProcess(target)
            .Where(static element => element.Current.ControlType == ControlType.MenuItem
                && element.Current.IsEnabled && !element.Current.IsOffscreen)
            .ToArray();
        var inPopup = menuBounds.IsEmpty || menuBounds.Width <= 0 || menuBounds.Height <= 0
            ? []
            : visibleItems.Where(element => menuBounds.IntersectsWith(element.Current.BoundingRectangle)).ToArray();
        var scopedItems = inPopup.Length > 0 ? inPopup : visibleItems;
        return scopedItems
            .OrderBy(static element => element.Current.BoundingRectangle.Top)
            .ThenBy(static element => element.Current.BoundingRectangle.Left)
            .Take(MaxUiElements).ToArray();
    }

    private static string MenuDiagnostics(AcceptanceTarget target, AutomationElement? menu)
    {
        var menuNodes = new List<AutomationElement>();
        var visibleItems = new List<AutomationElement>();
        foreach (var element in ElementsForProcess(target))
        {
            try
            {
                if (element.Current.ControlType == ControlType.Menu) menuNodes.Add(element);
                if (element.Current.ControlType == ControlType.MenuItem && !element.Current.IsOffscreen) visibleItems.Add(element);
            }
            catch (ElementNotAvailableException) { }
        }
        string SafeId(AutomationElement item, bool menuNode)
        {
            try
            {
                var id = item.Current.AutomationId;
                return menuNode && id.Length > 0 || id.StartsWith("ContextAction.", StringComparison.Ordinal)
                    ? id : Fingerprint(CurrentName(item));
            }
            catch (ElementNotAvailableException) { return "stale"; }
        }
        string boundsText;
        try { boundsText = menu is null ? "empty" : RectText(menu.Current.BoundingRectangle); }
        catch (ElementNotAvailableException) { boundsText = "stale"; }
        return $"menuBounds={boundsText}; visible menu nodes={menuNodes.Count} [{string.Join(',', menuNodes.Take(8).Select(item => SafeId(item, menuNode: true)))}]; visible menu items={visibleItems.Count} [{string.Join(',', visibleItems.Take(16).Select(item => SafeId(item, menuNode: false)))}]; owned top-level HWNDs={target.GetOwnedTopLevelWindows().Length}";
    }

    private static AutomationElement? FindByAutomationId(AcceptanceTarget target, string id)
    {
        var matches = target.FindElements(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id), MaxUiElements);
        return matches.Count == 0 ? null : matches[0];
    }

    private static AutomationElement? ExpandAndFindChannel(AcceptanceTarget target, string network, string channel)
    {
        var rows = target.FindElements(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem), MaxUiElements);
        var root = rows.FirstOrDefault(row => TreeItemSearchText(row).Contains(network, StringComparison.Ordinal));
        if (root is not null && root.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var patternObject)
            && patternObject is ExpandCollapsePattern pattern && pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
        {
            try { pattern.Expand(); } catch (ElementNotAvailableException) { }
        }
        return FindChannelRows(target, network, channel).FirstOrDefault();
    }

    private static string TreeItemSearchText(AutomationElement item)
    {
        try
        {
            var names = new List<string> { CurrentName(item) };
            var itemRuntimeId = item.GetRuntimeId();
            foreach (var text in item.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                     .Cast<AutomationElement>().Take(80))
            {
                var owningTreeItem = GetAncestors(text, 8).FirstOrDefault(static ancestor =>
                    ancestor.Current.ControlType == ControlType.TreeItem);
                if (owningTreeItem is not null && owningTreeItem.GetRuntimeId().SequenceEqual(itemRuntimeId))
                    names.Add(CurrentName(text));
            }
            return string.Join(' ', names);
        }
        catch (ElementNotAvailableException) { return CurrentName(item); }
    }

    private static async Task<AutomationElement?> WaitForChannelAsync(AcceptanceTarget target, string network, string channel, int timeoutSeconds)
    {
        return await WaitElementAsync(() => ExpandAndFindChannel(target, network, channel), timeoutSeconds).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForDemoFixtureAsync(AcceptanceTarget target, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var stableOrder = string.Empty;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var rowsReady = ExpandAndFindChannel(target, "AlphaNet", "#alpha") is not null
                && ExpandAndFindChannel(target, "AlphaNet", "#lounge") is not null
                && ExpandAndFindChannel(target, "AlphaNet", "#history-00") is not null
                && ExpandAndFindChannel(target, "BetaNet", "#general") is not null
                && ExpandAndFindChannel(target, "BetaNet", "#lounge") is not null;
            if (rowsReady)
            {
                var order = $"{FindChannelRows(target, "AlphaNet", "#alpha").Length}:"
                    + $"{FindChannelRows(target, "AlphaNet", "#history-00").Length}:"
                    + FindChannelRows(target, "BetaNet", "#general").Length;
                if (!string.Equals(order, stableOrder, StringComparison.Ordinal))
                {
                    stableOrder = order;
                    stableSince = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(400))
                {
                    return true;
                }
            }
            else
            {
                stableOrder = string.Empty;
                stableSince = DateTime.UtcNow;
            }
            await Task.Delay(200).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> WaitForAutomatedFixtureAsync(AcceptanceTarget target, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var stableSince = DateTime.UtcNow;
        var wasReady = false;
        while (DateTime.UtcNow < deadline)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var ready = ExpandAndFindChannel(target, "AlphaNet", "#alpha") is not null
                && ExpandAndFindChannel(target, "AlphaNet", "#general") is not null;
            if (ready && wasReady && DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(400)) return true;
            if (ready != wasReady)
            {
                wasReady = ready;
                stableSince = DateTime.UtcNow;
            }
            await Task.Delay(200).ConfigureAwait(false);
        }
        return false;
    }

    private static string UiaTreeSummary(AcceptanceTarget target)
    {
        var elements = target.FindElements(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition, MaxUiElements);
        var treeItems = elements.Where(static element => element.Current.ControlType == ControlType.TreeItem).ToArray();
        string[] knownLabels = ["AlphaNet", "BetaNet", "#alpha", "#general", "#lounge", "#history-00"];
        var fixtureLabels = elements.SelectMany(element => knownLabels.Where(label =>
                CurrentName(element).Contains(label, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).ToArray();
        var treeRows = treeItems.Take(50).Select(row =>
        {
            var searchText = TreeItemSearchText(row);
            var label = knownLabels.FirstOrDefault(candidate => searchText.Contains(candidate, StringComparison.Ordinal));
            var network = GetAncestors(row, 8).Select(CurrentName)
                .FirstOrDefault(candidate => candidate.Contains("AlphaNet", StringComparison.Ordinal)
                    || candidate.Contains("BetaNet", StringComparison.Ordinal));
            return $"{label ?? Fingerprint(searchText)}@{(network?.Contains("AlphaNet", StringComparison.Ordinal) == true ? "AlphaNet" : network?.Contains("BetaNet", StringComparison.Ordinal) == true ? "BetaNet" : "unknown")}";
        }).ToArray();
        var alphaLabelElements = elements.Where(element => CurrentName(element).Contains("#alpha", StringComparison.Ordinal)).Take(8).Select(element =>
        {
            var nearestTreeItem = GetAncestors(element, 8).FirstOrDefault(static ancestor => ancestor.Current.ControlType == ControlType.TreeItem);
            var networks = GetAncestors(element, 12).Select(CurrentName)
                .Select(static name => name.Contains("AlphaNet", StringComparison.Ordinal) ? "AlphaNet"
                    : name.Contains("BetaNet", StringComparison.Ordinal) ? "BetaNet" : null)
                .Where(static name => name is not null).Distinct(StringComparer.Ordinal).Take(2);
            return $"{ControlClass(element)}@{(nearestTreeItem is null ? "no-TreeItem" : Fingerprint(CurrentName(nearestTreeItem)))} /net={string.Join(",", networks)}";
        }).ToArray();
        return $"Bounded UIA tree summary: treeItems={treeItems.Length}; recognized disposable fixture labels={string.Join(',', fixtureLabels)}; "
            + $"TreeItem fixture ancestry={string.Join(',', treeRows)}; #alpha label element ancestry={string.Join(',', alphaLabelElements)}; "
            + string.Join("; ", elements.Take(80).Select(element =>
                $"{ControlClass(element)} name={Fingerprint(CurrentName(element))} id={Fingerprint(element.Current.AutomationId)} focusable={element.Current.IsKeyboardFocusable}"));
    }

    private static string PinnedListSummary(AcceptanceTarget target)
    {
        var list = FindByAutomationId(target, "PinnedConversations");
        var rows = GetPinnedButtons(target, list).ToArray();
        return $"Pinned List UIA: found={list is not null}; list name fingerprint={Fingerprint(CurrentName(list))}; button rows={rows.Length}; "
            + string.Join(';', rows.Take(16).Select(row =>
                $"name={Fingerprint(CurrentName(row))}, enabled={row.Current.IsEnabled}, offscreen={row.Current.IsOffscreen}, focusable={row.Current.IsKeyboardFocusable}"))
            + $"; model projection count/order={string.Join(',', ProjectionOrder(target))}";
    }

    private static AutomationElement[] FindChannelRows(AcceptanceTarget target, string network, string channel)
    {
        var rows = target.FindElements(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem), MaxUiElements);
        var candidates = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        foreach (var element in target.FindElements(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition, MaxUiElements))
        {
            if (!CurrentName(element).Contains(channel, StringComparison.Ordinal)) continue;
            AutomationElement? row = element.Current.ControlType == ControlType.TreeItem
                ? element
                : GetAncestors(element, 8).FirstOrDefault(static ancestor => ancestor.Current.ControlType == ControlType.TreeItem);
            if (row is null || !TreeItemBelongsToNetwork(row, network)) continue;
            try { candidates[string.Join(",", row.GetRuntimeId())] = row; }
            catch (ElementNotAvailableException) { }
        }
        foreach (var row in rows)
        {
            if (!CurrentName(row).Contains(channel, StringComparison.Ordinal) || !TreeItemBelongsToNetwork(row, network)) continue;
            try { candidates[string.Join(",", row.GetRuntimeId())] = row; }
            catch (ElementNotAvailableException) { }
        }
        return candidates.Values.ToArray();
    }

    private static AutomationElement[] GetTreeItemSiblings(AutomationElement row)
    {
        var parent = GetAncestors(row, 8).FirstOrDefault(static ancestor => ancestor.Current.ControlType == ControlType.TreeItem);
        if (parent is null) return [];
        var parentRuntimeId = parent.GetRuntimeId();
        return parent.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
            .Cast<AutomationElement>().Take(MaxUiElements)
            .Where(candidate => GetAncestors(candidate, 8).FirstOrDefault(static ancestor =>
                ancestor.Current.ControlType == ControlType.TreeItem) is { } nearest
                && nearest.GetRuntimeId().SequenceEqual(parentRuntimeId))
            .ToArray();
    }

    private static bool TreeItemBelongsToNetwork(AutomationElement row, string network) =>
        CurrentName(row).Contains(network, StringComparison.Ordinal)
        || GetAncestors(row, 8).Any(ancestor => CurrentName(ancestor).Contains(network, StringComparison.Ordinal));

    private static AutomationElement? FindPinnedButton(AcceptanceTarget target, FixtureConversation fixture)
    {
        var pinList = FindByAutomationId(target, "PinnedConversations");
        return GetPinnedButtons(target, pinList).FirstOrDefault(button => IsFixtureName(CurrentName(button), fixture));
    }

    private static IEnumerable<AutomationElement> GetPinnedButtons(AcceptanceTarget target, AutomationElement? list)
    {
        if (list is null) return [];
        return list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().Take(MaxUiElements);
    }

    private static bool HasPinnedRow(AcceptanceTarget target, FixtureConversation fixture) => FindPinnedButton(target, fixture) is not null;

    private static bool IsFixtureName(string accessibleName, FixtureConversation fixture) =>
        accessibleName.Contains(fixture.Network, StringComparison.Ordinal)
        && accessibleName.Contains(fixture.Name, StringComparison.Ordinal);

    private static bool IsFirstPinned(AcceptanceTarget target, FixtureConversation fixture)
    {
        var row = GetPinnedButtons(target, FindByAutomationId(target, "PinnedConversations")).FirstOrDefault();
        return row is not null && IsFixtureName(CurrentName(row), fixture);
    }

    private static int CountPinnedRows(IEnumerable<AutomationElement> rows, FixtureConversation fixture) =>
        rows.Count(row => IsFixtureName(CurrentName(row), fixture));

    private static int CountPinnedRows(AcceptanceTarget target, FixtureConversation fixture) =>
        CountPinnedRows(GetPinnedButtons(target, FindByAutomationId(target, "PinnedConversations")).ToArray(), fixture);

    private static string[] ProjectionOrder(AcceptanceTarget target)
    {
        var line = ReadTelemetry(target).LastOrDefault(static value => value.StartsWith("APP_PIN_PROJECTION ", StringComparison.Ordinal));
        if (line is null) return [];
        var match = ProjectionRegex.Match(line);
        if (!match.Success) return [];
        var order = match.Groups["order"].Value;
        return string.IsNullOrEmpty(order) ? [] : order.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<bool> ClickElementAsync(AcceptanceTarget target, AutomationElement element, bool rightButton)
    {
        _lastClickDiagnostic = "not evaluated";
        if (!await EnsureElementVisibleAsync(element, 3).ConfigureAwait(false))
        {
            _lastClickDiagnostic = "element could not be brought into the visible viewport";
            return false;
        }
        if (!element.Current.IsEnabled || element.Current.IsOffscreen)
        {
            _lastClickDiagnostic = $"menu item enabled={element.Current.IsEnabled}; offscreen={element.Current.IsOffscreen}";
            return false;
        }
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            _lastClickDiagnostic = $"menu item bounds empty={bounds.IsEmpty}";
            return false;
        }
        var point = ClickPoint(element);
        if (!Contains(bounds, point.X, point.Y))
        {
            _lastClickDiagnostic = "UIA-selected point is outside the target element bounds; no input sent";
            return false;
        }
        target.EnsureForeground();
        var pointOwner = target.GetPointOwnerPid(point.X, point.Y);
        if (pointOwner != target.ProcessId)
        {
            _lastClickDiagnostic = $"WindowFromPoint owner PID={pointOwner}; expected target PID={target.ProcessId}; no input sent";
            return false;
        }
        await Task.Delay(80).ConfigureAwait(false);
        var input = SendMousePair(target, point.X, point.Y, rightButton);
        _lastClickDiagnostic = $"SendInput {input.Sent}/2; error={input.Win32Error}; {input.Description}";
        return input.Sent == 2;
    }

    private static async Task<bool> EnsureElementVisibleAsync(AutomationElement element, int timeoutSeconds)
    {
        try
        {
            if (!element.Current.IsOffscreen) return true;
            if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var patternObject)
                && patternObject is ScrollItemPattern pattern)
                pattern.ScrollIntoView();
            return await WaitUntilAsync(() => !element.Current.IsOffscreen, timeoutSeconds).ConfigureAwait(false);
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static string MenuItemSummary(AcceptanceTarget target)
    {
        var items = ElementsForProcess(target)
            .Where(static element => element.Current.ControlType == ControlType.MenuItem && element.Current.IsEnabled && !element.Current.IsOffscreen)
            .Select(item =>
        {
            var name = CurrentName(item);
            var known = new[] { "Pin conversation", "Unpin conversation", "Move to top", "Activate", "Channel Properties…", "Close view without PART" }
                .FirstOrDefault(label => name.Contains(label, StringComparison.OrdinalIgnoreCase));
            var automationId = item.Current.AutomationId;
            return $"{(known ?? Fingerprint(name))}:id={(automationId.StartsWith("ContextAction.", StringComparison.Ordinal) ? automationId : Fingerprint(automationId))}:focusable={item.Current.IsKeyboardFocusable}";
        }).Take(16);
        return $"UIA context menu items: {string.Join(';', items)}";
    }

    private static InputResult SendMousePair(AcceptanceTarget target, int x, int y, bool rightButton)
    {
        target.EnsureForeground();
        if (!target.OwnsPoint(x, y)) return new InputResult(0, 0, "WindowFromPoint did not resolve to target PID");
        if (!NativeMethods.SetCursorPos(x, y)) return new InputResult(0, Marshal.GetLastWin32Error(), "SetCursorPos failed");
        Thread.Sleep(50);
        target.EnsureForeground();
        var flags = rightButton
            ? new[] { NativeMethods.MouseRightDown, NativeMethods.MouseRightUp }
            : new[] { NativeMethods.MouseLeftDown, NativeMethods.MouseLeftUp };
        var input = flags.Select(NativeMethods.NativeInput.MouseButton).ToArray();
        var sent = NativeMethods.SendInput((uint)input.Length, input, NativeMethods.SizeOfInput);
        return new InputResult((int)sent, sent == input.Length ? 0 : Marshal.GetLastWin32Error(), "native mouse button pair");
    }

    private static InputResult SendKeyPair(AcceptanceTarget target, ushort key, bool extended)
    {
        target.EnsureForeground();
        var extension = extended ? NativeMethods.KeyExtended : 0;
        var down = new[] { NativeMethods.NativeInput.Key(key, extension) };
        var downSent = NativeMethods.SendInput(1, down, NativeMethods.SizeOfInput);
        if (downSent != 1) return new InputResult((int)downSent, Marshal.GetLastWin32Error(), "native key down was incomplete");
        Thread.Sleep(60);
        var up = new[] { NativeMethods.NativeInput.Key(key, extension | NativeMethods.KeyUp) };
        var upSent = NativeMethods.SendInput(1, up, NativeMethods.SizeOfInput);
        return new InputResult((int)(downSent + upSent), upSent == 1 ? 0 : Marshal.GetLastWin32Error(), "native key down/up pair with separated transitions");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try { if (predicate()) return true; }
            catch (ElementNotAvailableException) { }
            await Task.Delay(100).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<T?> WaitElementAsync<T>(Func<T?> finder, int timeoutSeconds) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try { if (finder() is { } value) return value; }
            catch (ElementNotAvailableException) { }
            await Task.Delay(100).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<string[]> WaitForTelemetryAsync(AcceptanceTarget target, Func<string[], bool> predicate, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var lines = ReadTelemetry(target).ToArray();
            if (predicate(lines)) return lines;
            await Task.Delay(80).ConfigureAwait(false);
        }
        return ReadTelemetry(target).ToArray();
    }

    private static string[] ReadTelemetry(AcceptanceTarget target)
    {
        try
        {
            if (!File.Exists(target.TelemetryPath)) return [];
            using var stream = new FileStream(target.TelemetryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return [.. lines];
        }
        catch (IOException) { return []; }
    }

    private static bool HasMessagePair(IEnumerable<string> lines, string down, string up)
    {
        var values = lines.ToArray();
        var downIndex = Array.FindIndex(values, line => line.Contains($"WIN32_MESSAGE id={down}", StringComparison.Ordinal));
        var upIndex = Array.FindIndex(values, downIndex + 1, line => line.Contains($"WIN32_MESSAGE id={up}", StringComparison.Ordinal));
        return downIndex >= 0 && upIndex > downIndex;
    }

    private static bool HasRecordPair(IEnumerable<string> lines, string down, string up)
    {
        var values = lines.ToArray();
        var downIndex = Array.FindIndex(values, line => line == down);
        var upIndex = Array.FindIndex(values, downIndex + 1, line => line == up);
        return downIndex >= 0 && upIndex > downIndex;
    }

    private static AutomationElement[] ElementsForProcess(AcceptanceTarget target)
    {
        var values = new List<AutomationElement>();
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, target.ProcessId);
        foreach (var hwnd in target.GetOwnedTopLevelWindows())
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root.Current.ProcessId == target.ProcessId) values.Add(root);
                values.AddRange(root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>());
            }
            catch (ElementNotAvailableException) { }
            if (values.Count >= MaxUiElements) break;
        }
        return [.. values.Take(MaxUiElements)];
    }

    private static IEnumerable<AutomationElement> GetAncestors(AutomationElement element, int maximum)
    {
        var walker = TreeWalker.ControlViewWalker;
        var parent = walker.GetParent(element);
        for (var count = 0; parent is not null && parent != AutomationElement.RootElement && count < maximum; count++)
        {
            yield return parent;
            parent = walker.GetParent(parent);
        }
    }

    private static string[] GetAncestorClasses(AutomationElement element, int maximum) =>
        GetAncestors(element, maximum).Select(ControlClass).ToArray();

    private static string ControlClass(AutomationElement? element) => element is null
        ? "none"
        : $"{element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)}:{(string.IsNullOrWhiteSpace(element.Current.ClassName) ? "no-class" : "class")}";

    private static AutomationElement? FocusedAutomationElement()
    {
        try { return AutomationElement.FocusedElement; }
        catch (ElementNotAvailableException) { return null; }
    }

    private static string FocusFingerprint() => Fingerprint(CurrentName(FocusedAutomationElement()));

    private static string CurrentName(AutomationElement? element)
    {
        try { return element?.Current.Name ?? string.Empty; }
        catch (ElementNotAvailableException) { return string.Empty; }
    }

    private static string RectText(Rect rectangle) => rectangle.IsEmpty
        ? "empty"
        : $"{rectangle.Width:0}x{rectangle.Height:0}";

    private static (int X, int Y) ClickPoint(AutomationElement element)
    {
        try
        {
            if (element.Current.ControlType == ControlType.TreeItem)
            {
                var itemName = CurrentName(element);
                var text = element.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                    .Cast<AutomationElement>()
                    .FirstOrDefault(candidate =>
                    {
                        var name = CurrentName(candidate);
                        return name.Length > 0 && (itemName.Contains(name, StringComparison.Ordinal)
                            || name.Contains(itemName, StringComparison.Ordinal));
                    });
                if (text is not null && text.TryGetClickablePoint(out var textPoint))
                    return ((int)Math.Round(textPoint.X), (int)Math.Round(textPoint.Y));
            }
            if (element.TryGetClickablePoint(out var point))
                return ((int)Math.Round(point.X), (int)Math.Round(point.Y));
        }
        catch (ElementNotAvailableException) { }
        return Center(element.Current.BoundingRectangle);
    }

    private static (int X, int Y) Center(Rect rectangle) =>
        ((int)Math.Round(rectangle.Left + rectangle.Width / 2), (int)Math.Round(rectangle.Top + rectangle.Height / 2));

    private static bool Contains(Rect rectangle, int x, int y) =>
        !rectangle.IsEmpty && x >= rectangle.Left && y >= rectangle.Top
        && x < rectangle.Right && y < rectangle.Bottom;

    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 6)).ToLowerInvariant();
    }

    private static string FixtureFingerprint(FixtureConversation fixture) => Fingerprint($"{fixture.Network} · {fixture.Name}");

    private static string SafeTail(IReadOnlyList<string> lines, int count) =>
        string.Join("; ", lines.TakeLast(count).Select(static line => line.Length > 500 ? line[..500] : line));

    private static bool TryGetViewStatePair(IEnumerable<string> lines, string fingerprint,
        out ViewStateSnapshot before, out ViewStateSnapshot after)
    {
        var values = lines.ToArray();
        var afterIndex = -1;
        after = default;
        for (var index = values.Length - 1; index >= 0; index--)
        {
            if (TryParseViewState(values[index], "after", fingerprint, out after))
            {
                afterIndex = index;
                break;
            }
        }
        if (afterIndex < 0)
        {
            before = default;
            return false;
        }
        for (var index = afterIndex - 1; index >= 0; index--)
        {
            if (TryParseViewState(values[index], "before", fingerprint, out before)) return true;
        }
        before = default;
        return false;
    }

    private static bool TryParseViewState(string line, string phase, string fingerprint, out ViewStateSnapshot state)
    {
        var match = ViewStateRegex.Match(line);
        if (match.Success && match.Groups["phase"].Value == phase && match.Groups["target"].Value == fingerprint
            && int.TryParse(match.Groups["entries"].Value, out var entries)
            && int.TryParse(match.Groups["unread"].Value, out var unread)
            && int.TryParse(match.Groups["composerBytes"].Value, out var composerBytes))
        {
            state = new ViewStateSnapshot(entries, unread, match.Groups["important"].Value == "true",
                composerBytes, match.Groups["composer"].Value, match.Groups["open"].Value == "true",
                match.Groups["view"].Value, match.Groups["active"].Value);
            return true;
        }
        state = default;
        return false;
    }

    private static bool TryParsePinnedActivation(IEnumerable<string> lines, string fingerprint, out ViewStateSnapshot state)
    {
        foreach (var line in lines.Reverse())
        {
            var match = PinnedActivationRegex.Match(line);
            if (!match.Success || match.Groups["target"].Value != fingerprint
                || int.TryParse(match.Groups["entries"].Value, out var entries) is false
                || int.TryParse(match.Groups["unread"].Value, out var unread) is false
                || int.TryParse(match.Groups["composerBytes"].Value, out var composerBytes) is false)
                continue;
            state = new ViewStateSnapshot(entries, unread, match.Groups["important"].Value == "true",
                composerBytes, match.Groups["composer"].Value, match.Groups["open"].Value == "true",
                match.Groups["view"].Value, match.Groups["active"].Value);
            return true;
        }
        state = default;
        return false;
    }

    private static bool IsPinnedReopenEvent(string line, string fingerprint)
    {
        var match = PinnedActivationRegex.Match(line);
        return match.Success
            && string.Equals(match.Groups["target"].Value, fingerprint, StringComparison.Ordinal)
            && string.Equals(match.Groups["wasOpen"].Value, "false", StringComparison.Ordinal)
            && string.Equals(match.Groups["open"].Value, "true", StringComparison.Ordinal);
    }

    private static bool SameConversationState(ViewStateSnapshot left, ViewStateSnapshot right,
        bool compareComposer, bool compareActive = true) =>
        SameConversationData(left, right, compareComposer)
        && (!compareActive || left.ActiveFingerprint == right.ActiveFingerprint);

    private static bool SameConversationData(ViewStateSnapshot left, ViewStateSnapshot right, bool compareComposer) =>
        left.EntryCount == right.EntryCount && left.UnreadCount == right.UnreadCount
        && left.Important == right.Important && left.ViewFingerprint == right.ViewFingerprint
        && (!compareComposer || left.ComposerBytes == right.ComposerBytes
            && string.Equals(left.ComposerFingerprint, right.ComposerFingerprint, StringComparison.Ordinal));

    private static string FormatConversationState(ViewStateSnapshot state) =>
        $"open={state.IsOpen}, viewFingerprint={state.ViewFingerprint}, activeFingerprint={state.ActiveFingerprint}, entries={state.EntryCount}, unread={state.UnreadCount}, important={state.Important}, composerBytes={state.ComposerBytes}, composerFingerprint={state.ComposerFingerprint}";

    private static bool AddFail(AcceptanceReport report, string scenario, string control, string action,
        string input, string state, string ui, string details = "")
    {
        report.Add(scenario, control, report.TargetProcessId, action, input, state, ui, 0, "FAIL", "Level 1 — Native OS input", details);
        return false;
    }

    private static bool AddBlocked(AcceptanceReport report, string scenario, string control, string details)
    {
        report.Add(scenario, control, report.TargetProcessId, "acceptance action", "not sent", "not evaluated", "not evaluated", 0, "BLOCKED", details: details);
        return false;
    }

    private static void AddNotRun(AcceptanceReport report, string scenario, string action, string reason) =>
        report.Add(scenario, "not inspected", report.TargetProcessId, action, "not sent", "not evaluated", "not evaluated", 0, "NOT RUN", details: reason);

    private static void AddNotRunIfMissing(AcceptanceReport report, string scenario, string action, string reason)
    {
        if (report.Steps.Any(step => string.Equals(step.ScenarioId, scenario, StringComparison.Ordinal))) return;
        AddNotRun(report, scenario, action, reason);
    }

    private sealed record FixtureConversation(string Network, string Name);
    private readonly record struct ViewStateSnapshot(int EntryCount, int UnreadCount, bool Important,
        int ComposerBytes, string ComposerFingerprint, bool IsOpen, string ViewFingerprint, string ActiveFingerprint);
    private sealed record InputResult(int Sent, int Win32Error, string Description);
    private sealed record ContextMenuOpenResult(bool Opened, string Details, AutomationElement? Menu = null);
}
