# Phase 54R6 — Two-client synchronized pins acceptance closeout

## Primary outcome

**`EXTERNAL_EXECUTION_BLOCKER`**. Phase 54 remains open. No authenticated two-client synchronization or operator-performed desktop journey was available to verify in this execution. The disposable R6 runner attempts are recorded as blocked and are not substituted for account-synchronized evidence. Phase 55 is not eligible to start.

## Pre-edit repository gate

| Repository | Required start | Observed start | Branch | Worktree | Local tracking divergence |
|---|---|---|---|---|---|
| Client `D:\dev\nexIRC\nexIRC5` | `6aa971e01368a9feb772416528b1857ce4a1a491` | `6aa971e01368a9feb772416528b1857ce4a1a491` | `main` | clean | `origin/main`: 0 ahead / 0 behind |
| Server `D:\dev\nexIRC\nexIRCServer` | `fe4776855a318fecdf1def39ba5a7ded913c3dd7` | `fe4776855a318fecdf1def39ba5a7ded913c3dd7` | `main` | clean | `origin/main`: 0 ahead / 0 behind |

Both repositories matched the expected R5 state. No `.phase` marker was created. No fetch, pull, push, branch operation, reset, rebase, merge, cherry-pick, stash, clean, or worktree creation was performed. Production client/server source was not changed.

## Fresh R6 desktop evidence

| Invocation | Target and input source | Result | Observation |
|---|---|---|---|
| Automated demo, `--timeout 20` | Runner-owned `nexIRC.Desktop.exe`; requested native automation/UIA; no authenticated session | `BLOCKED` (`UI54-RUNNER`) | The runner did not observe a top-level client window before its startup deadline. PID/window identity remained unavailable; no action gate ran. See `phase54r6-demo-automated.json` and `.md`. |
| Guided manual demo, `--timeout 10` | Runner-owned `nexIRC.Desktop.exe`, PID `12832`, session `1`, `Default` desktop, integrity RID `8192`; human checkpoints require console input | `BLOCKED` (`UI54-MANUAL-START`) | A `nexIRC 5` window was validated, but `Console.IsInputRedirected` prevented the operator checkpoint. No manual input was acknowledged and no scenario action was performed. See `phase54r6-manual.json` and `.md`. |
| Desktop inventory | Computer-use app inventory | `BLOCKED` | The helper inventory returned no native apps; no attached nexIRC window or operator-ready target was exposed. |

The runner-owned demo process was cleaned up. No nexIRC desktop process remained after the runs. No independent Client A/Client B pair was present. Authentication, same-account association, production server connectivity, capability negotiation, initial synchronized pins, server mutation receipts, and propagation were therefore **not established**. No credentials were entered or collected.

### Acceptance gates

| Gate/assertion | R6 result | Evidence |
|---|---|---|
| Genuine authenticated Client A and B on the actual server | **BLOCKED** | No client sessions or account association were available to inspect. |
| Initial pin convergence and stable destination identities | **NOT RUN** | No synchronized clients. |
| A's active destination, composer focus/selection, unread state, transcript, draft, and view fingerprints | **NOT RUN** | No authenticated A to observe. |
| Remote MOVE_TOP acceptance on B and convergence on A | **NOT RUN** | No B action or server propagation. Focus, order, selection identity, and preservation assertions remain unverified in R6. |
| Remote UNPIN acceptance on B and removal on A | **NOT RUN** | No B action or server propagation. Active-view, PART, focus fallback, stale-index, duplicate-row, transcript, and draft assertions remain unverified in R6. |
| Nonactive remote UNPIN while A composes elsewhere | **NOT RUN** | No synchronized clients. |
| Operator-performed mouse and keyboard journey | **BLOCKED** | Console input was redirected; the manual runner's checkpoint could not be acknowledged. |
| Close pinned view without PART, reopen same identity, preserve transcript/unread/draft | **NOT RUN in R6** | The R5 demo result is retained historical evidence only. |
| Runtime UIA, accessible names, keyboard navigation, pinned focus | **NOT RUN in R6** | The R5 UIA and input results remain retained historical evidence only. |

## Evidence levels and retained R5 results

- **Fresh native Windows input automation:** None reached an action checkpoint in R6. The automated launch timed out before HWND validation.
- **Fresh runtime UI Automation:** No authenticated target was observed in R6. The manual report validated a runner-owned demo window but stopped before inspection/action checkpoints.
- **Fresh genuine two-process/two-session server synchronization:** None. No authenticated sessions, account association, capability evidence, server acceptance, or propagated mutation was recorded.
- **Fresh human/operator input:** None. R6 manual mode recorded the redirected-console block.
- **Synthetic and process regression evidence:** The Phase 54R server process filter passed 4/4. These tests are process-level regression evidence, not authenticated desktop clients.
- **Retained R5 evidence:** `artifacts/phase54r5-automated.*` records native mouse/keyboard PIN, UNPIN, and MOVE_TOP, destination activation, pinned close/reopen, draft/transcript/unread preservation, and runtime UIA accessibility using a runner-owned disposable demo. Remote focus after a second-client mutation was `NOT RUN`. `artifacts/phase54r5-manual.*` records the redirected-console block. These are historical R5 results, not new R6 execution.
- **Retained synchronization/protocol evidence:** Existing Phase 54 server/client persistence, multi-device, capability, fallback, and InspIRCd compatibility results remain as described in the preceding report. They do not prove the missing desktop focus journey.

No genuine product defect was reproduced. No production fix or test change was made. The two-client focus and view-preservation questions remain acceptance gaps rather than confirmed defects.

## Validation

Fresh builds on the unchanged R5 production source:

| Build | Result |
|---|---|
| Client solution Debug | Succeeded, 0 warnings, 0 errors |
| Client solution Release | Succeeded, 0 warnings, 0 errors |
| Server solution Debug | Succeeded on a clean serial rerun, 0 warnings, 0 errors |
| Server solution Release | Succeeded, 0 warnings, 0 errors |
| Acceptance runner Debug | Succeeded, 0 warnings, 0 errors |
| Acceptance runner Release | Succeeded, 0 warnings, 0 errors |

The first parallel Debug build pair caused two transient MSBuild file-lock retry warnings because both solutions compile shared client outputs. Both builds succeeded; the server Debug build was rerun serially and completed with zero warnings/errors.

| Fresh test run | Passed | Skipped | Failed |
|---|---:|---:|---:|
| Core | 147 | 0 | 0 |
| Networking | 82 | 0 | 0 |
| Application | 226 | 1 expected benchmark | 0 |
| UI acceptance runner tests | 5 | 0 | 0 |
| Focused pinned conversation identity regression | 2 | 0 | 0 |
| Server `Phase54R` process regression filter | 4 | 0 | 0 |

The retained R5 full Server suite result is 114 passed, 0 skipped, 0 failed; it was not rerun because server code was unchanged. No fresh full Server suite was run in R6.

## Privacy and cleanup

The two fresh runner reports contain only executable/window metadata, result classifications, and the console/startup blockers. They contain no authentication credentials, resume tokens, private account identifiers, conversation or composer text, raw input values, or screen coordinates. No attached telemetry, screenshots, runtime stores, or server logs were committed. Runner-owned nexIRC processes were cleaned up. Server production code was untouched.

## Shortest operator path to finish

1. Use the procedure in [the Windows UI acceptance guide](../docs/testing/Phase54-Windows-UI-Acceptance.md#phase-54r6-operator-completion) on an unlocked interactive Windows desktop with a console whose input is not redirected.
2. Start two independent nexIRC 5 processes, authenticate both normally to the same synchronized account on the actual server, and record sanitized process/session and capability evidence. Prepare two stable pins and the transcript/unread/draft preservation fixture on A.
3. Attach the existing manual runner to A. Have the operator focus each target and acknowledge the checkpoints only after B performs real UI MOVE_TOP and UNPIN actions. Confirm server acceptance and propagation independently.
4. Complete the remaining operator mouse/keyboard, close/reopen, accessibility, transcript, unread, and draft checkpoints on those same authenticated clients. Save sanitized R6 JSON/Markdown and operator notes; rerun any focused regressions affected by a confirmed defect.

## Decision

`EXTERNAL_EXECUTION_BLOCKER`; Phase 54 remains **open**. The missing acceptance is specifically authenticated two-client server propagation and remote focus/preservation for MOVE_TOP and UNPIN, plus the operator-performed journey. Phase 55 must not begin.
