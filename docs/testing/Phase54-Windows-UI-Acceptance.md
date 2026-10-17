# Phase 54 Windows UI acceptance

This guide runs the `nexIRC.UiAcceptance` executable against a disposable test client. It records JSON and Markdown evidence. It does not close Phase 54 by itself; the mouse, keyboard, remote-focus, and close/reopen gates pass only when the report contains matching WPF and application-state evidence.

## Requirements

- Windows with an interactive, unlocked desktop in the same logon session as the console.
- The client and acceptance runner must run at the same integrity level.
- .NET 10 SDK and the repository's normal restore/build dependencies.
- Keep the target nexIRC window visible. If another application covers the control point, automated input stops without sending input.
- Use disposable fixture data. The built-in `--demo` client creates `AlphaNet` and `BetaNet` with `#alpha`, `#general`, and history rows; it is not connected to a synchronized account or IRC server.

## Build

From the client repository root:

```powershell
dotnet build .\nexIRC5.sln -c Debug
```

The runner is `tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll`. Reports below go into `artifacts\`; create that directory before using it.

## Diagnostic mode

```powershell
New-Item -ItemType Directory -Force .\artifacts | Out-Null
dotnet .\tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll `
  --mode diagnostic --timeout 15 --report .\artifacts\phase54r5-diagnostic.json
```

Without `--pid`, the runner starts and owns a `--demo` client, validates its executable/PID/HWND/title/session/desktop/integrity, inspects the live WPF UIA tree, and performs harmless F24 and row-selection input qualification. With an explicit `--pid`, diagnostic mode is read-only and does not change focus or inject input.

## Automated mode

```powershell
dotnet .\tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll `
  --mode automated --timeout 15 --report .\artifacts\phase54r5-automated.json
```

Automated mode launches its own `--demo` process by default. To exercise a synchronized test account, launch client A yourself with the opt-in probe, then pass its exact PID, executable path, and existing telemetry file. Both paths use runtime UIA geometry and native `SendInput`, then require WPF action telemetry, pinned-list state, projection order, and preservation snapshots before marking a UI action PASS. The local journey uses `AlphaNet/#alpha` and `AlphaNet/#general` so the second pin creates a real reorder case. Remote updates from client B are reported NOT RUN in automated mode; use attached manual mode for that separately witnessed two-client check.

```powershell
$clientExe = (Resolve-Path .\src\nexIRC.Desktop\bin\Debug\net10.0-windows\nexIRC.Desktop.exe).Path
$tracePath = Join-Path (Resolve-Path .\artifacts).Path 'phase54r5-attached-input.jsonl'
$clientA = Start-Process -FilePath $clientExe `
  -ArgumentList @("--phase54r3-input-probe=$tracePath") -PassThru
# Connect A to the disposable synchronized test account and ensure AlphaNet/#alpha and AlphaNet/#general exist.
dotnet .\tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll `
  --mode automated --pid $clientA.Id --client $clientExe --telemetry $tracePath `
  --timeout 30 --report .\artifacts\phase54r5-attached-automated.json
```

The explicit PID path validates that the HWND belongs to the supplied PID and that its executable, window title, session, desktop, and integrity level match. The runner never closes an attached process. Use a disposable synchronized account: automated mode sends actual input and performs local PIN/UNPIN/MOVE_TOP actions in that client.

If automated input fails because another window owns the point or the target cannot receive focus, read the scenario and diagnostics in the report. Do not treat a partial journey as acceptance.

## Guided manual mode

Run this in an interactive PowerShell console:

```powershell
dotnet .\tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll `
  --mode manual --timeout 15 --report .\artifacts\phase54r5-manual.json
```

The runner opens a test-owned demo client and prints one checkpoint at a time. Perform the requested mouse or keyboard action in nexIRC, then return to the console and press Enter. Enter only ends the checkpoint; it cannot produce a pass. The runner requires the expected WPF action, matching fixture fingerprint, UIA/catalog state, and—where applicable—transcript, unread, importance, active-view, view-identity, and composer-fingerprint evidence. The report contains hashes and byte counts, never composer text.

The local demo checklist first removes its seeded Alpha pin, then guides mouse PIN, second PIN, MOVE_TOP, UNPIN, and pinned destination activation using `AlphaNet/#alpha` and `AlphaNet/#general`. It then guides keyboard navigation, keyboard context-menu PIN, pinned activation, MOVE_TOP, UNPIN, and a final keyboard-navigation check. Finally it closes Alpha through the pinned row's `Close view without PART` context action, reopens it from Pinned, compares the same view identity and saved draft fingerprint, and checks for duplicate rows. Remote focus scenarios are NOT RUN in this isolated demo.

Press Ctrl+C at any time to cancel. The runner closes only the process it launched and removes its temporary telemetry directory. A redirected/noninteractive console is recorded as BLOCKED because manual checkpoints require an operator.

## Attached remote-focus mode

The remote focus checklist uses a dedicated client A that was launched with the opt-in WPF probe. Use a disposable synchronized account and a second client B; keep both in the same interactive Windows session. Start client A with the probe enabled, for example:

```powershell
New-Item -ItemType Directory -Force .\artifacts | Out-Null
$clientExe = (Resolve-Path .\src\nexIRC.Desktop\bin\Debug\net10.0-windows\nexIRC.Desktop.exe).Path
$tracePath = Join-Path (Resolve-Path .\artifacts).Path 'phase54r5-attached-input.jsonl'
$clientA = Start-Process -FilePath $clientExe `
  -ArgumentList @("--phase54r3-input-probe=$tracePath") -PassThru
```

Connect client A and B to the disposable account, pair them using the normal application flow, and pin at least two conversations. Then attach the manual runner to A:

```powershell
dotnet .\tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll `
  --mode manual --pid $clientA.Id --client $clientExe --telemetry $tracePath `
  --timeout 30 --report .\artifacts\phase54r5-remote-focus.json
```

Attached manual mode validates the selected process and performs no input injection, focus changes, launch, or cleanup on it. Follow the prompt to focus a non-first pinned row in A, move that same row to the top from B, and use the keyboard in A. Then focus another pinned row in A, remove that same row from B, and use the keyboard in A. The report checks A's projection, UIA focus identity, remaining-row focus after removal, keyboard event evidence, and accessible pinned rows. Windows telemetry does not distinguish physical input from UIA input; the report records this limitation.

The attached mode does not automate B's remote mutation. It requires the operator to submit MOVE_TOP and UNPIN from B. The demo launch command cannot exercise synchronization. Therefore these steps remain acceptance gates until the two-client interactions actually pass.

## Reading reports

Each invocation writes the requested `.json` report and a sibling `.md` summary. Results are `PASS`, `FAIL`, `BLOCKED`, or `NOT RUN`; evidence levels keep native input separate from runtime UIA and synthetic tests. A report with any unverified mandatory desktop gate leaves Phase 54 open. This Phase 54R5 run retains its reviewed, sanitized JSON and Markdown reports under `artifacts/phase54r5-*` as acceptance evidence; remove attached telemetry and any other temporary reports before committing.
