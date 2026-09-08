# nexIRC 5 — Phase 22: Bidirectional History Navigation and Anchor Jumps

## Closeout

Phase 22 extends the Phase 21 history substrate into a navigable timeline. A
channel or query can now page in either direction from canonical local history,
jump to an indexed server message id or deterministic timestamp anchor, use a
bounded remote `AROUND` request when local history cannot answer, and return to
the current canonical edge without waiting for server synchronization.

The accepted result is Outcome A: bidirectional local navigation, forward
`AFTER`, bounded message/time navigation, Return to Latest, live-follow state,
and restrained WPF integration are implemented. Remote behavior remains
capability- and target-gated; a server response never gets treated as an exact
msgid match unless the canonical identity is actually present.

The closeout also adds deterministic `forward-pagination` coverage for a
500-row local edge followed by two remote `AFTER` pages, and adversarial tests
for duplicate-only `AFTER` progress and an `AROUND` response that omits the
requested msgid.

## Repository baseline

The repository was `D:\dev\nexIRC\nexIRC5` on `main` at:

`5b006598c981afe6ababc4399c3c870e8c442a37`

with subject `Implement nexIRC 5 Phase 21 history pagination and coverage`.
The authoritative starting worktree was clean and `origin/main...HEAD` was
`0 ahead / 0 behind`; this differed from the pasted Phase 21 note, which said
`1 ahead / 0 behind`. No fetch, pull, merge, rebase, reset, or push was used.

## Architecture and behavior

### Directional coverage

`HistoryCoverageLedger` remains the single runtime ledger. It now records
independent backward and forward frontiers, local newer/end evidence,
canonical oldest/newest anchors, the currently projected window, directional
exhaustion, zero-progress termination, and the direction of a pending request.
Coverage remains keyed by `(NetworkId, durable conversation key)`, so a msgid
from one network or query cannot satisfy another conversation.

Generation changes discard ephemeral remote frontiers and request state while
leaving canonical local windows intact. Canonical observations are reconciled
after indexed reads, remote playback, navigation context, and live message
merges. Projected boundaries are recorded separately from canonical boundaries.

### Forward local pagination

`LoadNewerMessagesAsync` consults the indexed log store first and uses
`HistoryPageRequest.After` plus `AfterDurableSequence` for equal-time rows.
Pages are canonicalized, deduplicated only by authoritative network-scoped
server msgid, and appended in ascending transcript order while trimming the
oldest projected edge when necessary. No remote request is sent while a local
newer page exists.

Once the local edge is reached, a safe remote request uses:

`CHATHISTORY AFTER <target> <selector> <limit>`

The selector is the strongest available trustworthy frontier: msgid when
advertised and available, otherwise an authoritative server timestamp. A
returned ordinary row advances the forward frontier; an unchanged selector,
empty non-terminal page, or duplicate-only response terminates conservatively
instead of creating a busy loop. An explicit `draft/chathistory-end` applies
only to the active forward frontier.

### Message and timestamp navigation

`HistoryNavigationRequest` and `HistoryNavigationResult` provide typed
navigation outcomes. Local msgid and timestamp requests use the Phase 1Z
index/context APIs and materialize at most the configured bounded context
window. The WPF transcript marks the selected row with a temporary anchor
state and the dispatcher restores the selected row into view.

Timestamp selection is deterministic:

1. `Around` chooses an exact timestamp if present.
2. Otherwise it chooses the nearest timestamp.
3. An equal-distance tie chooses the earlier canonical record.
4. `AtOrAfter` and `AtOrBefore` retain their directional meanings.

When local history cannot answer, a registered session must advertise usable
CHATHISTORY plus a compatible `MSGREFTYPES` selector. It sends bounded
`AROUND msgid=...` or `AROUND timestamp=...`, drains serialized semantic
presentation, flushes the durable logger, and re-queries canonical history.
If the requested msgid is still absent, the result is typed context/miss
information and never an exact match. Shuffled remote rows are sorted before
projection.

### Context rows and canonical identity

Rows tagged `draft/chathistory-context` are recognized centrally. They do not
consume the ordinary CHATHISTORY limit and cannot advance a backward or
forward frontier by themselves. They retain their batch/provenance metadata
when persisted and projected as surrounding context.

Rows with no msgid remain distinct; timestamp, sender, text, and provenance do
not become a guessed duplicate key. Equal timestamps use durable sequence and
the existing canonical tie-break ordering. The JSONL schema remains version 2:
Phase 22 adds runtime navigation state and request fields, not persisted
navigation state or a schema migration.

### Query identity and safe targets

Navigation addresses use network, scope, conversation kind, and the durable
conversation key. A query's current nickname may change without invalidating
that key. Remote query history is refused unless the current session has a
safe identity binding; a historical nickname alone is never used as proof of
the current server target. Conflicting account evidence remains isolated.
Channel targets remain the channel's validated current name.

### Return to Latest and live follow

`ReturnToLatestAsync` reads a bounded newest window from local history in page
units, projects it, clears only the temporary navigation marker/indicator, and
restores `IsFollowingLive`. It does not erase canonical history or request
remote synchronization. The view exposes separate `IsViewingHistory`,
`IsFollowingLive`, `HasNewerLiveMessages`, and loading/status properties.

Live messages arriving while the user is viewing history are canonicalized and
persisted but do not move the viewport to the bottom. They set a small newer
message indicator. Historical projections do not mark unread/activity state.
Return to Latest deliberately moves to the live edge; ordinary bottom-scroll
state continues to control automatic follow behavior.

The existing WPF layout gained only Load Older, Load Newer, and Return to Latest
controls. Input, scroll, and navigation mutations route through the existing
serialized workspace dispatcher and action router. There is no new history
window, custom renderer, natural-language date parser, or full-text search
subsystem. A typed API is provided for validated date/msgid input; no free-form
date dialog was added in this phase.

Prepending older rows retains the Phase 21 extent-delta behavior. Appending
newer historical rows does not force the viewport downward. Explicit jumps
select and scroll the anchor. Return to Latest scrolls to the end. The
transcript remains bounded at 500 rows while canonical history stays durable.

### Request coordination and exact gaps

The existing single active CHATHISTORY tracker continues to own TARGETS,
reconnect recovery, BETWEEN repair, BEFORE, AFTER, AROUND, and manual context.
The application-level per-conversation pagination task coalesces repeated
older/newer requests. No second network scheduler or priority system was
introduced: recovery/exact repair remain governed by the existing tracker, and
explicit navigation fails/coalesces at that boundary rather than racing a
correctness request. Local forward paging naturally consumes canonical rows
created by exact BETWEEN repair, so it does not issue a broad AFTER for a gap
that is already durable.

## Validation

The focused Phase 22 protocol tests pass 3/3, and the application navigation
tests pass 7/7. The final Phase 20/21/22 application-scoped runs pass 14/14
(4 + 3 + 7). The Phase 1R–1Z/20/21 application-scoped regression matrix also
passes: 38 tests across the available phase classes. Full Core passes 86/86;
full Networking passes 42/42.

The new WPF semantic smoke
`--demo --ui-smoke history-navigation` passes these checks:

* 1,000 persisted rows open as the newest 50;
* msgid 425 jumps locally with zero IRC traffic;
* a 700.5 timestamp tie selects message 700;
* five local newer pages reach message 1,000 without remote traffic;
* live messages 1,001–1,005 remain canonical while history is being viewed;
* Return to Latest restores the live edge and follow mode;
* one shuffled remote AROUND retrieves an exact missing-local anchor;
* repeating that jump is local-only;
* unread and current channel state remain unchanged.

The deterministic `--demo --ui-smoke forward-pagination` smoke also passes:
seven local newer pages make no IRC request, then two remote `AFTER` requests
advance from `forward-500` to `forward-525` to `forward-550`; the end marker
suppresses a repeated request. The Phase 21 `history-pagination` smoke remains
the backward local/remote proof. `chathistory`, `history-gap-repair`,
`history-discovery`, `history-identity` (the query-navigation smoke),
`history-navigation`, and `sustained-interactivity` all pass. The local-only
msgid/time/newer/return paths are covered by the Phase 22 application tests
with an unregistered fake session and durable local store; no IRC traffic is
observed.

The retained `burst-fairness` smoke remains timing-bound in this host: the
Phase 22 Release run timed out at its existing 15-second burst-tail boundary.
The Phase 21 baseline reproduced the same timeout (also with a 500-row bounded
tail), so this is recorded as an environment/baseline limitation rather than
presented as a passing result.

The local navigation fixture is intentionally bounded. No dedicated 50,000-row
benchmark was added: indexed lookups and context materialization are bounded by
the requested context/page, and the WPF projection remains capped at 500 rows.
The repository-wide Application test invocation previously exhibited the Phase
21 whole-host stall with no assertion output; grouped project/focused runs and
the deterministic WPF smoke are the actionable validation paths, and the stall
is reported rather than hidden.

The existing credential-free Libera smoke is supplemental and does not join
random public channels. If the live run does not advertise
`draft/chathistory`, remote AFTER/AROUND claims are sourced from the
deterministic fake-server protocol/application harnesses rather than inferred
from Libera. The live run passed TLS, CAP/registration, JOIN/NAMES,
server-time, extended-join/account observation, WHOIS numerics through 318,
and clean QUIT/disconnect. Libera advertised no `draft/chathistory`, so no
live remote history request was attempted.

The Release solution build passes with 0 warnings and 0 errors, and
`dotnet format nexIRC5.sln --no-restore --verify-no-changes` passes. The
repository-wide Application test command still reproduces the Phase 21
aggregate-host stall: after assembly start and one explicitly skipped
performance test, it produced no assertion output for roughly 100 seconds and
was interrupted safely. This does not occur in the phase-scoped runs above.

The main defect found during validation was a hot-path regression from
publishing navigation/coverage work for every live row. Coverage reconciliation
is now checkpointed for ordinary live traffic, remains immediate for active
historical/playback paths, and WPF viewport restoration is coalesced and only
armed while viewing history. Phase 1R then passed again; the baseline comparison
was used to distinguish the remaining aggregate/burst timing behavior.

## Limitations and Phase 23 recommendation

Runtime remote frontiers/exhaustion are generation-scoped and are not persisted.
The typed navigation APIs are ready for a future validated date/msgid input
surface, but Phase 22 does not add a new dialog. A future phase can add a
search-result-to-anchor adapter, richer navigation history, and a small input
control while preserving this canonical navigation boundary. The current WPF
surface intentionally exposes Load Older/Newer and Return to Latest; typed
Go-To date/msgid actions are available to the application layer but have no
free-form input dialog yet. No dedicated 50,000-record benchmark was added;
the bounded fixture, Phase 1R endurance, and sustained-interactivity metrics
were used instead. Full-text search, large transcript virtualization, and
automatic server-history download remain out of scope.

Phase 23 should add a strict, UTC-explicit date/msgid input control and a
search-result-to-anchor adapter, then revisit aggregate-host endurance and
large-history performance without weakening the single-request lifecycle or
the 500-row projection bound.
