# Phase 38 — Application Testhost Recovery and Process-Kill Resume

## Outcome

`APPLICATION_GATE_AND_PROCESS_CRASH_RESUME_VERIFIED`

`PHASE37_ACCEPTANCE_GAP_CLOSED`

Phase 38 repaired the full Application test stall, closed the remaining Phase
37 acceptance gap, and exercised protected resume with deterministic storage
faults and real Windows client-process termination. The existing wire protocol
remains `nexirc/resume=1`.

## Baseline and repository scope

Work began on client `main` at
`1d4c748f385a00e9560ab301361b1283539dac43` and server `main` at
`71beef22bcb83f627b306d8854aeddf7c933624b`. Both worktrees were clean before
changes. No fetch, pull, merge, rebase, reset, branch checkout, or push was
performed. All changes are local.

The client repository is `D:\dev\nexIRC\nexIRC5`; the server repository is
`D:\dev\nexIRC\nexIRCServer`. The crash helper is a test-only executable in the
server repository and references the client Core, Networking, and Application
projects.

## Application testhost investigation

The reproduction command was:

```powershell
dotnet test tests/nexIRC.Application.Tests/nexIRC.Application.Tests.csproj -c Debug --no-restore --logger 'console;verbosity=minimal'
```

Discovery completed and the minimal logger produced no per-test progress. A
hang-diagnostics run used:

```powershell
dotnet test tests/nexIRC.Application.Tests/nexIRC.Application.Tests.csproj -c Debug --no-restore --logger 'console;verbosity=minimal' --blame-hang-timeout 3m
```

The blame sequence and dump were written at 15:03 on 2026-09-26. Testhost PID
28336 had completed 189 test cases; the only incomplete case was
`Phase1TTests.AcceleratedEnduranceKeepsReconnectChurnAndRetainedStateBounded`.
Thus execution had passed discovery and entered ordinary test bodies. The
apparent “nothing after discovery” was caused by quiet logging, not a testhost
that had failed to dispatch its first test. The async dump showed
`AppendHistoryChurnAsync` awaiting `AppendAsync` at the first of 2,400 history
records while the JSONL writer was idle waiting for channel input. The process
was waiting on asynchronous completion rather than spinning.

The smallest reliable reproduction was the single Phase 1T endurance test.
Earlier Phase 37 evidence also reproduced a stall from starting commit
`c2475358dfc98142231b4f052d7dc6d653007936`. Its preserved sequence ended in
Phase 1R, without a dump. Both long-session cases exercise the same bounded
history writer; the Phase 38 dump identifies the precise stranded-write
mechanism.

`JsonlConversationLogStore` used a bounded channel with `DropWrite`. When full,
`TryWrite` could report success while the channel silently discarded the
pending record. `AppendAsync` then awaited that record's completion source
forever, while the sole writer had no record to complete. This also meant a
history append could be lost while its caller remained indefinitely blocked.

The writer now uses `BoundedChannelFullMode.Wait`, and `AppendAsync` uses
`WriteAsync` so it either waits for bounded capacity or fails when the writer
has closed. Accepted records still complete through their existing completion
source. No tests were removed, skipped, reordered, or weakened, and global test
parallelism settings were not changed.

## Application acceptance and Phase 37 closure

After the fix, the complete Application project passed three consecutive runs:

| Run | Passed | Skipped | Total | Duration |
| --- | ---: | ---: | ---: | ---: |
| 1 | 209 | 1 | 210 | 1m 59s |
| 2 | 209 | 1 | 210 | 2m 10s |
| 3 | 209 | 1 | 210 | 2m 20s |

Each run completed discovery, test execution, and normal testhost shutdown. A
later full client solution run also passed Application 209/210 in 2m 22s. The
single skip is the existing opt-in Phase 1O performance benchmark, guarded by
`NEXIRC_RUN_PHASE1O_PERFORMANCE=1`; it was run separately with that variable
enabled and passed 1/1.

The full client solution gate passed Core 131/131, Networking 77/77, and
Application 209 passed, 1 skipped, 210 total. Networking includes the Phase
31–37 reconnect, history, native resume, fallback, TLS, account-binding, and
redaction coverage. The Application suite includes protected persistence and
the Phase 37 state-store cases. The opt-in benchmark was also separately
verified. These results close the Phase 37 acceptance gap that had remained
because the full Application project stalled.

The Networking gate exposed a completion-order race in permanent resume
rejection. `RequestNativeResumeAsync` could return its rejected result before
the old protected state had been removed and a fresh server-issued session had
been adopted and persisted. Rejection completion now happens after that
fallback work. The existing
`PermanentResumeRejectionAdoptsFreshSessionAndReplacesProtectedState` test
passed three consecutive focused runs and the full Networking suite passed.

## Process-kill harness

`NexIrc.ResumeCrashClient` is a separate test executable. The parent server test
starts it as a child process, waits on flushed, non-secret markers and named
event barriers, then uses `Process.Kill(entireProcessTree: true)`. The helper
uses Windows DPAPI CurrentUser protection, TLS with the generated certificate
thumbprint pinned, and SASL for the test account. The password is sent through
stdin, never as an argument. Test-only store observations contain only stage,
generation, and replay boundary. Internal server acknowledgement and
replay-event barriers are unset in ordinary server configuration; normal
production protocol behavior is unchanged.

The real-process test ran on Windows against a loopback TLS server with an
in-memory server session store. It established one logical session and
repeatedly terminated fresh client processes while carrying the same profile
and protected state directory through restart.

### Issuance and rotation results

| Kill boundary | Observed durable/server state | Recovery result |
| --- | --- | --- |
| Initial issuance, before serialization | No state file; the server had issued an orphaned session | Fresh ordinary authenticated issuance remained possible |
| Initial issuance, after atomic persistence | Generation 1, no pending token, boundary `s0` | Fresh process resumed the same logical session |
| Issuance handler complete | Helper waited until a fresh store load returned a usable generation-1 record | Process was killed after durable state was observable; restart resumed the same session |
| `SESSION ROTATE` received, before parsing/persistence | The raw-line callback held the child before rotation state was processed | Process death did not offer an unpersisted replacement |
| Rotation before serialization | Primary remained current generation 1 at `s1`, with no pending token; server remained generation 1 | Old durable credential remained authoritative |
| Pending token atomically committed, before ACK | Primary held current generation 1 plus pending generation 2 at `s1`; server held the same logical session with pending generation 2 | Restart resumed using the pending token and continued replay on the same logical session |
| ACK sent; server promoted, response withheld | Client still had the old/pending pair (4/5); server had promoted to generation 5; the test barrier withheld `ACK OK` | Child died before receiving success; next process resumed with the pending credential |
| `ACK OK` received, before local cleanup | Client still had old/pending pair (5/6); server had promoted to generation 6 | Child died at the raw receive marker; restart resumed with the pending credential |
| Promoted primary replaced, before temporary cleanup callback | Primary had generation 7 with no pending token; server had retired the prior token | Child was killed at the atomic-replacement marker; restart loaded the promoted token |
| Promoted record cleanup complete | Generation 8, no pending token, no `.tmp` file; server generation 8 had no pending token | Child was killed after temporary cleanup; another fresh process resumed successfully at generation 9 |

The acknowledgement-sent case also proves the stronger reachable state in
which the server has promoted but the client has not received `ACK OK`. The
test does not separately suspend the server before it begins processing an
already-written ACK; the earlier pending-commit/pre-ACK case proves recovery
from the corresponding unchanged server-pending state.

The full server run also exposed a timing assumption in the existing
`LiveEventDuringReplayIsHeldUntilCompletionAndThenFollowsTheTip` test. It used
a 5 ms delay to publish during replay and waited for a momentary zero-connection
count while the client used a 1–5 ms reconnect backoff. Depending on scheduling,
the event joined the replay snapshot or the test missed the disconnect window.
The test now uses an internal server callback to pause immediately after the
first replay event is sent, publishes the live event while replay is held, and
then releases replay. Its reconnect policy gives the test a bounded,
observable disconnect interval. Three consecutive focused runs passed without
timing sleeps.

The process test uncovered a server correctness defect: token rotation stored
the pending token hash in the session but did not index that hash back to its
logical session. A process restart correctly loaded and offered the pending
token, but the server rejected it as unknown. `SessionStore.PrepareTokenRotation`
now indexes the pending hash to the same logical session while the overlap is
active. After the change, pending-token restart recovery completed replay and
preserved the same session identity.

### Replay crash results

The same process-kill test stopped the child at these replay boundaries:

1. Resume accepted and first replay line received but before processing it;
   the durable boundary remained `s4`.
2. First replay event committed; the durable boundary advanced to `s5`, then
   the child was killed.
3. Two events and then the third event committed, reaching `s7`; the child was
   killed before replay completion was reported.
4. A fresh process loaded `s7`, resumed the same logical session, and completed
   without replaying already-committed events. A later live event was received.

The replayed canonical message IDs remained
`phase38-mid-gap-1`, `phase38-mid-gap-2`, and `phase38-mid-gap-3`; sequence
boundaries advanced monotonically. The test asserts each expected message ID
once within its resumed client run and confirms the same server logical-session
ID. The protocol remains at-least-once across the accepted anchor; these tests
do not claim distributed exactly-once delivery. Durable local application
history projection is covered by the client Application suites, not by the
child process, which exercises Core session replay directly.

Repeated restart progression reached generation 9 while retaining the same
logical session and authoritative boundary `s7`. The protected directory
finished with one primary JSON record and at most one backup; no temporary or
quarantine artifacts remained. The server diagnostic assertion found no SASL
password in its diagnostics.

## Storage fault injection and corruption

Internal observation points allow deterministic exceptions before
serialization, after serialization, after temp-file creation, after write,
after write-through flush, before atomic replacement, after replacement, and
around temporary cleanup. The observation payload exposes no secret material.

Six injected pre-commit failures (before serialization, after serialization,
after temp creation, after write, after write-through flush, and immediately
before replacement) left the prior primary authoritative. A fresh store
instance recovered the old protected token and the temporary file was cleaned.
An injected exception after replacement still returned the committed new
primary on fresh load and retained exactly one backup with no temporary file.
Existing coverage also corrupted a primary with a valid backup and verified
fallback to the backup.

The corruption matrix covered truncated JSON, malformed JSON, unsupported
version, missing current protected token, pending generation not newer than
current, acknowledgement generation greater than current, blank boundary, and
mismatched network identity. Corrupt primary and backup together produced a
bounded corrupt result without selecting either record. Unsupported data was
reported as unsupported. Details did not include the test secret. The existing
Networking coverage additionally verifies ordinary fallback after DPAPI/test
protector failure, wrong network, and wrong account.

The fault hooks inject errors at deterministic store abstraction boundaries;
they do not emulate power loss, storage-controller reordering, or a partially
executed filesystem `File.Replace`. Replacement failure is approached from the
last deterministic pre-replacement boundary, and primary/backup selection is
tested separately.

## Security, compatibility, and regression results

- Windows DPAPI `CurrentUser` remains the production protection mechanism;
  network identity remains additional entropy.
- TLS was enabled and pinned to the generated certificate in the process
  harness; SASL authenticated account `alice` before resume.
- Test stdout contained redacted markers, not raw tokens, passwords, SASL
  payloads, or unprotected DPAPI contents. The server diagnostic audit found no
  SASL password.
- The server test verifies same logical-session identity, generation
  continuity, pending-token recovery, and monotonic replay boundaries.
- The complete client Core, Networking, and Application suites passed. This
  includes ordinary IRC fallback, native-resume rejection, TLS policy,
  account/network binding, history recovery, and relationship/reply/reaction
  coverage already present in those suites.
- The complete server suite passed 23/23, including the real-process test and
  existing TLS, SASL, replay, token, expiry, invalidation, persistence, and
  restart coverage.
- Debug and Release solution builds for both repositories succeeded with zero
  warnings and zero errors.
- `git diff --check` passed in both repositories.

The real process test is Windows-only because it relies on DPAPI and Windows
process/event APIs. The integration server is loopback and in-memory; it tests
client process death while preserving the server process, not server-process
restart or external-network behavior. Phase 38 adds no protocol version, UI
feature, clustering, replication, multi-device ownership, or cross-server
migration.

## Defects repaired and next phase

Phase 38 repaired three correctness issues: dropped bounded history writes
could strand callers; permanent resume rejection could complete before safe
fallback state was durable; and a pending rotation token could not be resolved
to its logical server session after client process death. Each fix is covered
by the full relevant regression gate and/or the real-process recovery test.

Phase 39 should extend fault/restart coverage to server-process recovery and
the real Application history projection path, then add a deterministic
pre-server-ACK-processing barrier case if that exact micro-boundary needs an
independent result. Keep the wire protocol at version 1 unless a concrete
compatibility defect requires otherwise.
