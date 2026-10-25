# Phase 54R9 — Two-client synchronized pins operator guide

This package prepares a real operator-run Windows acceptance session for nexIRC 5 synchronized pins. It does not authenticate clients, perform UI actions, or certify Phase 54. The operator uses two genuine WPF clients connected to the actual nexIRC Server. The default startup command launches both processes and then opens the guided checkpoints.

## Quick start

Open **Windows PowerShell** or **PowerShell 7** in a normal interactive console in the client repository:

```powershell
Set-Location 'D:\dev\nexIRC\nexIRC5'
& '.\tools\phase54\Start-Phase54R9.ps1'
```

The launcher validates the built client and UI acceptance runner, creates a unique run directory, starts two distinct client processes, enables the existing Phase 54R3 input probe on Client A, verifies each executable/PID/window/session, and starts the guided acceptance. It never starts a server instance. Sign both clients in through the normal nexIRC UI after they open.

If a required binary is missing, build it from the client repository root and rerun:

```powershell
dotnet build .\nexIRC5.sln -c Debug
```

The launcher refuses redirected input/output and non-console PowerShell hosts before creating a run or starting a client. Use a visible, unlocked interactive Windows session. It does not kill client processes on error or at completion. If client startup or window validation fails, the run directory contains `startup-status.json` with the bounded failure and any validated process identities.

### Reuse existing clients

Reuse is supported when the processes are distinct nexIRC 5 executables with usable windows in the current Windows session. Client A needs the opt-in probe file from its process startup for attached UIA/event observation. Pass its existing telemetry path only if that file belongs to the live A process and contains `PROBE_READY`. A telemetry file inside the client repository must be under the ignored `artifacts/phase54r9/` directory; the launcher rejects other in-repository paths.

```powershell
& '.\tools\phase54\Start-Phase54R9.ps1' `
  -ClientAProcessId <A-PID> -ClientATelemetryPath '<existing-local-input.jsonl>' `
  -ClientBProcessId <B-PID>
```

If reused A has no probe, omit `-ClientATelemetryPath`. The guide asks you to confirm that a supplied probe file belongs to the live A PID. If that cannot be verified, telemetry use and the attached UIA runner are disabled; operator checkpoints remain available. Do not create an empty file to imitate probe output.

To launch/attach clients without immediately entering the workflow, use `-PrepareOnly`; the launcher prints the exact `Invoke-Phase54R9Acceptance.ps1` and `Complete-Phase54R9.ps1` commands for that run. The normal one-command path runs both scripts automatically.

## Before acceptance

Use a disposable synchronized test account and harmless test conversations. Do not use the built-in `--demo` fixture. The launcher does not authenticate clients and does not enter credentials. Both clients must connect to the actual intended server and authenticate through the supported nexIRC UI.

The startup screen prints the A and B process IDs. Use Task Manager's Details view to identify which visible window belongs to each PID before labeling the windows. Confirm they are different processes, each exposes a usable `nexIRC 5` WPF window, and both run in the same interactive Windows session.

Establish the prerequisites through existing UI/session evidence:

1. Confirm A and B are connected to the actual intended server.
2. Confirm both sessions belong to the same authenticated account. Do not enter the account name or credential in PowerShell or the report. Similar conversation names do not prove account equality.
3. Confirm the documented capability on each client: `nexirc/resume`, `nexirc/state`, and `nexirc/pins` are established.
4. Confirm both clients show the same initial synchronized pin membership and order, with no duplicate rows.
5. Prepare at least four harmless test pins. On A, keep conversation X active with transcript and a known unread state; create a distinctive harmless unsent draft, leave the composer focused, and note selection/focus.
6. Record only opaque hex fingerprints or counts where available: pin order, active view, focused control, draft, transcript, unread state, view identity, and relevant JOIN/PART lifecycle state. Do not copy messages, draft text, account names, or credentials.

If a required prerequisite cannot be verified, mark it **UNVERIFIED** or **BLOCKED**. The guided workflow then leaves dependent acceptance claims without evidence.

## Guided checkpoint controls

The operator performs every mouse and keyboard action in the live WPF windows. The scripts instruct and record; they do not send input, submit PIN/MOVE_TOP/UNPIN actions, or treat acknowledgment as a pass.

Each checkpoint accepts:

- `P` — PASS
- `F` — FAIL
- `U` — UNVERIFIED
- `B` — BLOCKED
- `Q` — stop at this checkpoint and save a partial run

Non-pass results can include a short comment. Do not type account names, conversation names, message or draft contents, passwords, tokens, or resume credentials. Each prompt has a bounded wait (15 minutes by default; comments and fingerprint prompts have shorter limits). The existing runner also uses a bounded operator prompt. Ctrl+C cancels its attached session and writes its partial runner report; choosing `Q` is the preferred way to stop the PowerShell guide.

Operator answers are labeled **operator-reported**. The package keeps those separate from the launcher process checks, runner UI Automation, and bounded input-probe event summaries. The final report does not infer a PASS from a projection change, an Enter press, or a historical demo result.

## Remote MOVE_TOP

Keep A active on X with the composer focused and the harmless draft unsent. From B's actual WPF UI, use a real mouse or supported keyboard path to move a different, nonactive pinned test conversation to the top. Wait for convergence before reporting each gate.

The first checkpoint records whether the actual B-side action was performed or attempted. Then record these independently:

1. **Server acceptance:** PASS only when reliable existing server-side evidence or a correlated authenticated server ACK shows the mutation was accepted. A changed order, local rendering, or A/B convergence by itself is not server acceptance. The standard UI acceptance runner does not expose a correlated server receipt or server log. If the actual server's existing evidence is unavailable, mark this mandatory gate UNVERIFIED.
2. **Projection and convergence:** verify the requested order in B, the remote projection in A, eventual agreement between both clients, correct membership, and no stale or duplicate rows.
3. **Preservation on A:** verify X stays active; composer focus and selection are as expected; the unsent draft, transcript, unread state, and view identity are preserved; no unexpected PART or unrelated lifecycle change occurs.

After the preservation checkpoint, the guide asks for updated A/B pin-order fingerprints and A's active-view, focus, draft, transcript, unread, and view-identity fingerprints. These are stored as operator-reported `after-move` evidence for comparison with the initial baseline.

## Remote nonactive UNPIN

Keep A active on X with the composer focused and the unsent draft intact. From B's actual UI, unpin a different nonactive test conversation. Leave at least two pins for the runner's separate focus checks.

The first checkpoint records whether the actual B-side action was performed or attempted. Record server acceptance separately, then verify B's removal and A's remote projection converge without duplicates or stale rows. Confirm X remains active, A's composer focus/selection and draft survive, transcript/unread/view identity are preserved, and no unexpected PART or unrelated conversation lifecycle change occurs. This nonactive case is mandatory.

After the preservation checkpoint, the guide records the same safe A/B and A-state fingerprints as `after-unpin` evidence, which can be compared with the `after-move` snapshot.

The existing Phase 54 contract defines UNPIN as an account-catalog tombstone with order sequence zero. It does not define a new active-selection rule for unpinning the currently active conversation. Active-conversation UNPIN can be recorded as an optional observation if the applicable existing product contract requires it; do not invent a selection or PART expectation.

## Existing attached runner

When A's opt-in probe is ready, the guide invokes the existing `nexIRC.UiAcceptance` manual mode against A's exact PID, executable, and telemetry file. The runner validates process ownership, session, desktop and integrity, inspects A's WPF UI Automation tree, then guides two additional remote-focus actions:

1. Focus a nonfirst pinned row in A; use B to MOVE_TOP that exact row; use the keyboard once in A.
2. Focus a pinned row in A; use B to UNPIN that exact row; use the keyboard once in A and confirm focus moves to a remaining pinned row.

It records whether the projection changed, the focused UIA element stayed on the same row after reorder or moved to a remaining pinned row after removal, a keyboard event pair arrived, and accessible pinned rows remain unique and focusable. Those are runtime UIA/event observations. The runner does **not** verify server acceptance, same-account authentication, composer focus, active-conversation selection, draft, transcript, unread state, view identity, or PART behavior for the remote changes. The operator checkpoints cover those separate requirements.

## Mouse, keyboard, and accessibility

Complete the Gate C checkpoints on the authenticated clients using real WPF input:

- Click a pinned row and verify the selected destination.
- Right-click an unpinned test conversation and choose PIN.
- Right-click a nonfirst pinned row and choose MOVE_TOP.
- Right-click a nonactive pinned row and choose UNPIN.
- Navigate the conversation tree and Pinned list with the keyboard; verify focused and selected item state.
- Open a row context menu with Apps or Shift+F10 and choose a context action using the keyboard.
- Inspect accessible list and row names/roles, enabled/focusable state, UIA target, selected state, and duplicate absence.
- Recheck focus, draft, transcript, unread, view identity, and lifecycle state where the existing contract requires preservation.

The probe records bounded Win32/WPF event classes and app events, not raw key values or pointer coordinates. `APP_VIEW_STATE` and pinned-row records can contain short hashes and byte counts derived from view/draft state. Raw telemetry is therefore treated as local evidence even though the probe omits the underlying text. The final report uses only event-classification counts from relevant checkpoint intervals and never copies raw telemetry lines or telemetry hashes.

The historical R3 issue where mouse routing reported a Grid instead of the expected TreeViewItem and the incomplete Apps-key-to-WPF event delivery are still unresolved acceptance risks. If only a Win32 message appears without the expected WPF/context action, record FAIL or UNVERIFIED. Windows telemetry cannot prove whether an input event came from a physical device or UI Automation; the operator reports the actual input source.

## Close and reopen

The guide records A/B pin-order fingerprints before close, asks the operator to close A using the normal window close, and verifies that the exact A PID exited. It then launches a fresh A process with a new telemetry file so previous probe evidence is not overwritten. Sign in again through the supported UI, confirm capability, compare membership/order with still-connected B, and record A/B pin-order fingerprints after reopen. Check no duplicate or stale rows remain.

Treat the synchronized pin catalog as durable server state and UI focus/draft as transient process state. Focus or a draft is required to survive this restart only if the existing Phase 54 contract explicitly says so. Do not change product persistence semantics during acceptance.

## Reports and privacy

Each run uses a unique directory:

```text
artifacts/phase54r9/<run-id>/
```

The run contains `session.json`, an append-only `operator-checkpoints.jsonl`, the attached runner's JSON/Markdown output when available, and a sanitized report pair. Completion never overwrites an earlier report; it picks a new timestamped filename.

The repository ignores the entire R9 artifact directory. Keep raw JSONL telemetry and free-form comments there. The shareable report includes process identities, start/end repository snapshots, safe opaque operator fingerprints at initial and reopen stages, gate results, evidence provenance, and event-classification counts. It omits local repository paths, telemetry lines and hashes, free-form comments, message/draft contents, account identifiers, credentials, key values, and screen coordinates. Operators must still avoid entering sensitive text in comments.

The completion script reports `OPERATOR_PACKAGE_READY_ACCEPTANCE_PENDING` until all authenticated setup checkpoints pass and an operator confirms a real B-side MOVE_TOP or nonactive UNPIN attempt, or the attached runner observes its remote actions. It then reports `PARTIAL_DESKTOP_ACCEPTANCE` until an independent review verifies the evidence. An `UNVERIFIED` action prompt alone does not imply that a mutation occurred. The script never emits `SYNCHRONIZED_PINS_V1_ACCEPTED`. Phase 54 remains open and Phase 55 remains unauthorized until that review is complete.

## Package commands

The normal startup command runs preparation, guided acceptance, and report generation. To resume a preserved run or explicitly regenerate a sanitized report:

```powershell
& '.\tools\phase54\Invoke-Phase54R9Acceptance.ps1' -RunDirectory 'D:\dev\nexIRC\nexIRC5\artifacts\phase54r9\<run-id>'
& '.\tools\phase54\Complete-Phase54R9.ps1' -RunDirectory 'D:\dev\nexIRC\nexIRC5\artifacts\phase54r9\<run-id>'
```

The completion output prints the Markdown report path and machine-readable JSON path. Share the sanitized report pair for independent review; keep raw telemetry local.
