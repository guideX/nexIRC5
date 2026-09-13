# Phase 30 — Controlled InspIRCd 4 Capability Variance Matrix

## Primary outcome

`LOCAL_VARIANCE_ENVIRONMENT_BLOCKED`

The retained Phase 28 local InspIRCd 4 environment could not execute with an
approved portable/local runtime on this host. The retained launcher was run
with all seven retained variance profile names and failed during executable
resolution, before a server process was started. No local CAP, ISUPPORT,
message, history, relationship, reconnect, or durability behavior is claimed
by this phase.

This is an environment limitation, not a nexIRC interoperability failure. The
public InspIRCd 5 evidence from Phase 29 and the deterministic protocol,
networking, application, reconnect, and history tests remain valid evidence for
the behavior they cover.

## Environment and exact blocker

Repository: `D:\dev\nexIRC\nexIRC5`
Branch: `main`
Starting HEAD: `f92b2dd4131e65310c6b6b86dd7b87bb73c971c6`
Starting subject: `Verify nexIRC 5 interoperability with InspIRCd`

The retained environment was the Phase 28 disposable Windows launcher and
configuration template:

- launcher: `scripts/run-phase28-inspircd-interoperability.ps1`;
- configuration: `scripts/phase28/inspircd/inspircd.conf.template`;
- retained runtime location: `artifacts/phase28/server/runtime`;
- disposable run roots: `artifacts/phase28/server/runs`;
- transport intended by the profile: loopback plaintext with an ephemeral
  port;
- server lifecycle intended by the launcher: one bounded process per profile,
  stopped by recorded process ID and never installed as a service.

The runtime check on 2026-09-13 found:

- `artifacts/phase28/server/runtime` exists but contains no files and no
  `bin/inspircd.exe`;
- no `inspircd.exe` in the standard locations
  `C:\Program Files\InspIRCd\bin\inspircd.exe` or
  `C:\Program Files\InspIRCd\inspircd.exe`;
- `INSPIRCD_HOME` is unset;
- Docker and Podman are unavailable;
- WSL is present only as the Windows command, reports no installed
  distributions, and reports that WSL1 is unsupported by the current machine
  configuration; enabling Windows features or installing a distribution was
  out of scope;
- the approved portable `.NET` runtime used by the nexIRC harness is present
  (`dotnet` SDK `10.0.401`), but it is not an InspIRCd server runtime;
- no system-wide installation, PATH change, service, firewall change, feature
  enablement, or administrator action was attempted.

The retained launcher produced the exact failure below and exited with code 1:

```text
InspIRCd v4 runtime unavailable. No executable was found in the explicit
path/root, INSPIRCD_HOME, the ignored Phase 28 runtime area, or the standard
Windows package locations. Searched:
D:\dev\nexIRC\nexIRC5\artifacts\phase28\server\runtime\bin\inspircd.exe;
C:\Program Files\InspIRCd\bin\inspircd.exe;
C:\Program Files\InspIRCd\inspircd.exe.
```

The official Windows package previously selected by Phase 28 was
InspIRCd 4.12.0, but it was an administrator-required installer and no
executable was produced by the earlier user-scoped attempt. Because no local
daemon ran in this phase, the actual local InspIRCd version is **not
observed**. `InspIRCd-5` remains the actual version observed by the separate
Phase 29 public testnet run; it must not be substituted for the missing local
InspIRCd 4 evidence.

## Profiles attempted and CAP evidence

The launcher invocation requested the complete retained matrix:

1. `inspircd-local-full`
2. `inspircd-local-noecho`
3. `inspircd-local-nomsgid`
4. `inspircd-local-restricted-tags`
5. `inspircd-local-no-tags`
6. `inspircd-local-no-message-tags`
7. `inspircd-local-no-server-time`

These profiles were **not executed**. Runtime discovery failed before the
launcher entered its per-profile loop, so no profile received a port, config,
server process, client connection, CAP LS, CAP REQ, CAP ACK/NAK, or ISUPPORT
exchange. Consequently the exact local CAP advertisement is unobserved for
every profile, as shown below.

| Profile | Configured variance | CAP advertisement | Actual InspIRCd version | Execution |
| --- | --- | --- | --- | --- |
| Full/reference | all retained modules; client tags `all` | unobserved | unobserved | blocked before start |
| No echo | `ircv3_echomessage` omitted | unobserved | unobserved | blocked before start |
| No msgid | `ircv3_msgid` omitted | unobserved | unobserved | blocked before start |
| Restricted tags | `clientonlytags="known"` | unobserved | unobserved | blocked before start |
| No client-only tags | `clientonlytags="none"` | unobserved | unobserved | blocked before start |
| No message-tags | `ircv3_ctctags` omitted | unobserved | unobserved | blocked before start |
| No server-time | `ircv3_servertime` omitted | unobserved | unobserved | blocked before start |

The configured module/profile design is retained evidence of intended test
coverage only; it is not server behavior evidence.

## Capability-variance results

No local wire result exists for `CLIENTTAGDENY`, no-echo, no-msgid, the
combined no-echo/no-msgid case, history, missing parents, or capability
renegotiation. They are therefore classified as **environment-unobserved**,
not passed, failed, or unsupported-by-server.

### CLIENTTAGDENY

No local `CLIENTTAGDENY` advertisement or rejection was observed. The retained
deterministic Phase 26 fixtures remain authoritative for parsing exact,
wildcard, and exception forms and for refusing a locally known forbidden
relationship tag. No live claim is made for permitted tags, forbidden tags,
`+reply`, `+draft/react`, unknown `+nexirc/...` tags, TAGMSG, or tagged PRIVMSG
against InspIRCd 4.

### No echo-message

The no-echo profile did not run. No claim is made about local optimistic
presentation, canonical identity acquisition, duplicate suppression, pending
operation completion, or reconnect behavior in this profile. The existing
contract remains unchanged: local presentation identity is not promoted to a
canonical server identity without server evidence, and no msgid is invented.

### No canonical msgid

The no-msgid profile did not run. No live claim is made about ordinary
messages, replies, reactions, unreactions, reconnect, durable reopen, or
search/navigation in that profile. The existing conservative contract remains
in force: timestamp, display text, nick, or message order is not a substitute
for an opaque canonical msgid.

### No echo plus no msgid

The combined weak profile was not separately configured by the retained Phase
28 launcher and was not executed. No local evidence exists for that
combination. It must remain a reduced-functionality case until a portable
runtime is available; no unsafe correlation heuristic was added.

### History and CHATHISTORY

The retained Phase 28 design includes `chanhistory`, which is not by itself
evidence of IRCv3 `CHATHISTORY`. No local CAP or ISUPPORT was received, so
CHATHISTORY support form, reference types, limits, BATCH framing, historical
msgids, server-time, retained client tags, reaction-event history, reply
metadata, LATEST/BEFORE/AFTER, gap repair, or durable reopen were all
unobserved. The existing conservative gating remains: nexIRC must not send
CHATHISTORY unless that support is actually negotiated/detected.

### Missing parent

No local parent-missing child event could be produced because no server ran.
Recoverable-parent, unrecoverable-parent, delayed-parent, cross-conversation,
conflicting-identity, and reconnect-while-pending behavior therefore received
no new live evidence. Deterministic Phase 25/26 recovery and request-lifecycle
tests remain the available evidence. No parent-by-text, nearby-row, timestamp,
or cross-conversation fallback was introduced.

## Isolation, reconnect, and durability

Channel isolation and query/DM isolation were not live-tested in the local
matrix. The same is true for identical text in multiple conversations,
same-nick channel/query combinations, relationship targeting, capability
renegotiation across reconnect, stale-generation callbacks, pending requests,
JSONL deduplication, blank TAGMSG rows, synthetic IDs, and durable search or
navigation after reopen. No local evidence is claimed for these cases.

The Phase 29 public InspIRCd 5 run positively verified channel/query
relationships, reconnect, durable relationship state, durable search and
navigation, and no history request when CHATHISTORY was absent. The existing
deterministic suites cover the deeper generation and missing-parent contracts.
Those results are compared below but are not relabeled as local InspIRCd 4
evidence.

## Production defects and repairs

No production defect was found because the local variance matrix could not
execute. No production code, protocol behavior, application behavior, or UI
behavior was changed. No server-specific workaround, msgid-shape assumption,
or no-echo/no-msgid heuristic was added.

The only tracked changes for Phase 30 are this blocked-outcome document and
the `.gitignore` entry for ignored Phase 30 evidence. The launcher and
profiles remain reusable for a future run when an approved portable InspIRCd
4 runtime is supplied.

## Deterministic regression coverage

No new deterministic fixture was justified by live evidence in this blocked
phase. Existing coverage remains in place for:

- exact and wildcard `CLIENTTAGDENY` policy;
- malformed/unknown TAGMSG handling and no blank reaction rows;
- opaque server msgids and InspIRCd-style opaque ID syntax;
- reply/reaction identity and missing-parent recovery contracts;
- no-echo reaction presentation;
- repeated identical bodies and cross-network identity isolation;
- reconnect generation fencing and history request lifecycle.

## Comparison with Phase 27 and Phase 29

| Concern | Phase 27 Ergo | Phase 29 InspIRCd 5 | Phase 30 local InspIRCd 4 |
| --- | --- | --- | --- |
| Runtime | public TLS testnet | public TLS testnet | unavailable locally |
| Actual version | `ergo-v2.19.1` | `InspIRCd-5` | unobserved |
| message-tags | enabled | enabled | unobserved |
| echo-message | enabled | enabled | unobserved |
| canonical msgid | observed, opaque | observed, opaque | unobserved |
| server-time | enabled | enabled | unobserved |
| account-tag | enabled | enabled | unobserved |
| batch | enabled | enabled | unobserved |
| labeled-response | enabled | enabled | unobserved |
| CHATHISTORY | enabled | absent/unsupported | unobserved |
| event playback | enabled | absent/unsupported | unobserved |
| CLIENTTAGDENY | absent | absent | unobserved |
| replies/reactions | passed | passed | unobserved |
| channel/query isolation | passed | passed | unobserved locally |
| reconnect | passed | passed | unobserved locally |
| durable reopen/search | passed | passed | unobserved locally |

Phase 30 adds no contradictory evidence. It confirms only that the retained
local environment is still unavailable under the approved-runtime constraint.

## Validation and evidence

The retained launcher was executed as:

```powershell
pwsh -NoProfile -File .\scripts\run-phase28-inspircd-interoperability.ps1 -RunVarianceProfiles -OutputPath artifacts\phase30\inspircd4-environment-result.json -ComparisonPath artifacts\phase30\server-comparison.json
```

It returned exit code 1 with `SECOND_SERVER_ENVIRONMENT_BLOCKED` in the
machine-readable result. No remote Git operation was performed. No server
process or temporary profile process was started, so there was no local server
cleanup beyond the launcher's normal blocked-path result.

The local live matrix, live reconnect/history matrix, and UI relationship
smokes were not run because the required server could not start and no
production UI behavior changed. Safe deterministic validation did run against
the unchanged codebase:

- Phase 30 focused live tests: not executed; blocked before server start;
- Core: 118 passed;
- Networking: 46 passed;
- Application: 180 passed, 1 skipped;
- focused reply/reaction/history/navigation Application tests: 40 passed;
- isolated Phase 1R: 1 passed;
- Debug solution build: passed, 0 warnings, 0 errors;
- Release solution build: passed, 0 warnings, 0 errors;
- UI smoke: not applicable; no production relationship/UI change;
- reconnect/history live test: not executed; blocked before server start;
- `git diff --check`: run at closeout for the documentation-only change.

The first attempt to launch the three test projects concurrently was discarded
because the projects share build outputs. Its two file-lock errors were not
test failures; sequential reruns passed with the counts above.

Evidence:

- [inspircd4-environment-result.json](../artifacts/phase30/inspircd4-environment-result.json)
- [server-comparison.json](../artifacts/phase30/server-comparison.json)
- [Phase 28 retained launcher](../scripts/run-phase28-inspircd-interoperability.ps1)
- [Phase 28 retained configuration](../scripts/phase28/inspircd/inspircd.conf.template)

The Phase 30 JSON evidence is ignored and contains no credentials. The
comparison file embeds the already-sanitized Ergo reference metadata from the
existing ignored Phase 27 artifact; it contains no new public-network run.

## Remaining limitations and recommended next phase

The complete local capability-variance matrix, controlled `CLIENTTAGDENY`
wire behavior, no-echo/no-msgid live behavior, combined weak profile, local
history variants, local missing-parent reproduction, and capability changes
across reconnect remain unverified. These are environment limitations, not
nexIRC failures.

Recommended next phase: provide an approved, already-extracted portable
InspIRCd 4 runtime (including its matching modules) to the retained launcher,
then run the full matrix and add deterministic fixtures only for protocol
shapes or generic defects actually observed. Do not install system-wide or
enable Windows features solely to bypass this outcome.
