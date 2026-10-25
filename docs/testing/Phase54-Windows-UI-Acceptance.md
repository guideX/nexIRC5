# Phase 54 Windows UI acceptance

For the R9 operator-driven two-client synchronized pins handoff, start with [the Phase 54R9 operator guide](Phase54R9-Operator-Guide.md). It uses the attached runner below and adds operator-reported checkpoints for authentication, server acceptance, preservation, accessibility, and reopen convergence.

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

## Phase 54R6 operator completion

R6 remains blocked until an operator can run two authenticated nexIRC 5 clients against the same synchronized account on the actual server. Do not use `--demo` as either authenticated client. Use the product's supported setup to obtain two independent processes and authenticated sessions in the same interactive, unlocked Windows logon session. Keep credentials out of command lines, telemetry, reports, and screenshots.

Start client A with the opt-in probe and keep its telemetry file in `artifacts`:

```powershell
$clientExe = (Resolve-Path .\src\nexIRC.Desktop\bin\Debug\net10.0-windows\nexIRC.Desktop.exe).Path
$tracePath = Join-Path (Resolve-Path .\artifacts).Path 'phase54r6-client-a-input.jsonl'
$clientA = Start-Process -FilePath $clientExe -ArgumentList @("--phase54r3-input-probe=$tracePath") -PassThru
```

Start client B normally in a second process. Sign both clients in through the ordinary product flow to the same account and actual server. Confirm the server connection and synchronized-pin capability in each client and confirm the same initial pins on both. Record sanitized process metadata (PID, executable, HWND, Windows session, desktop, and account association classification); never record account names or credentials. Prepare two stable pinned destinations, a populated open transcript, a distinctive unsent draft, and a known unread state on A.

In an interactive PowerShell console whose input is not redirected, attach the runner to A:

```powershell
dotnet .\tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll `
  --mode manual --pid $clientA.Id --client $clientExe --telemetry $tracePath `
  --timeout 30 --report .\artifacts\phase54r6-attached-manual.json
```

Attached manual mode validates A and reads its WPF telemetry/UI Automation state; it sends no input and never launches or closes A. It prompts for the focused-row baseline, then waits while the operator uses B's real UI to MOVE_TOP that row. Repeat the guided baseline and remote action for UNPIN. Press Enter only after performing each instructed operator action. The runner checks projection change, focused pinned-row identity, a post-update keyboard event, and accessible rows. Its result is not a server acceptance receipt and does not measure active destination, composer selection/content, transcript, unread state, view identity, or PART activity; independently verify server acceptance/propagation and record those Gate A/B preservation checks before and after each mutation. The attached mode currently reports Gate A/B plus accessibility checks; the operator must also record Gate C's mouse, keyboard, close/reopen, transcript, unread, and draft checkpoints against the same authenticated clients in the accompanying acceptance notes. Do not count checkpoints as runner-verified unless the report contains the relevant evidence.

Complete Gate C on the authenticated clients: use real mouse input for pinned destination activation and pin management, invoke and operate a context menu with the keyboard, move a pin from B and observe A, unpin from B and observe A, close an open pinned view without PART, reopen the same identity, and verify transcript, unread, draft, accessible names, navigation, and focus. For each checkpoint, note the client, process/session, physical operator action, before/after state fingerprints, and verification source. Do not include message or composer text, raw key values, credentials, account identifiers, or screen coordinates. Keep the runner's JSON/Markdown report and a sanitized operator note under `artifacts/phase54r6-*`.

The ordinary `--mode manual` invocation without `--pid` launches a disposable demo client. It is useful for local control checks only and cannot satisfy authenticated two-client acceptance. The R6 run's manual checkpoint was blocked because the console input was redirected; its demo launch is not evidence for Gates A, B, or C.
