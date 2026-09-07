# nexIRC 5 Phase 1Z — Identity Evidence and History Anchor Indexing

Phase 1Z strengthens the boundary between IRC conversation identity and
nickname spelling, and adds a bounded anchor/index layer over the existing
JSONL history. The implementation remains deliberately conservative: a
nickname is useful routing evidence, but it is never promoted to a permanent
cross-session identity by itself.

## 1. Repository and baseline

The implementation was started in `D:\dev\nexIRC\nexIRC5` on branch `main`.
The expected Phase 1Y baseline was present:

* starting HEAD: `72a2580e07a4acf89232a835176ea8a8a3163786`
* subject: `Implement nexIRC 5 Phase 1Y IRCv3 history discovery and event playback`
* starting worktree: clean
* starting divergence: `0 ahead / 0 behind` relative to `origin/main`

No fetch, pull, merge, rebase, push, reset, or discard operation was used.
The live repository state was authoritative throughout.

Phase 1Y supplied the bounded CHATHISTORY request lifecycle, TARGETS
discovery, reconnect recovery, canonical server-playback merge, and the
historical current-state firewall. Phase 1Z builds on those existing paths.

## 2. Identity-evidence architecture

`IdentityEvidence.cs` introduces a typed, network-scoped representation:

* `ConversationIdentityEvidence` records nickname, optional account, optional
  user/host, generation, timestamp, source, and historical/live provenance.
* `ConversationIdentityEvidenceLedger` retains a bounded observation set per
  query and deliberately does not erase account evidence after logout.
* `ConversationIdentityEvidenceSnapshot` exposes bounded account/nickname sets,
  conflicting accounts, and the strongest observed evidence.
* `ConversationIdentityEvidencePolicy` returns typed outcomes rather than
  making ad-hoc UI comparisons.

The ledger is held by `QueryView` as two separate bounded stores: live
identity evidence and historical identity evidence. Historical observations
cannot update the live ledger. The live ledger is the only source used for
current query ownership and continuity decisions.

Evidence ordering is explicit:

1. existing exact conversation/view identity;
2. same network plus one matching non-empty account value;
3. bounded reconnect continuity already established by the application;
4. current-generation live nickname ownership;
5. nickname-only evidence;
6. conflicting or ambiguous evidence.

Account comparison is network-scoped and conservative. Account absence does
not match another account. Two different non-empty accounts produce a typed
conflict. Account values are bounded and normalized, but are not treated as a
global identity namespace.

The implementation records account-tag and ACCOUNT-command evidence, live
prefix evidence, reconnect continuity, and historical account/nickname/prefix
provenance. Existing extended-join/account parsing remains available through
the negotiated protocol state; no assumption is made that every server
provides it.

## 3. Identity safety and continuity rules

Network IDs are checked before evidence is compared. Equal account strings or
equal nicknames on different networks cannot match.

Nickname spelling alone remains replaceable evidence. It can reuse a current
live route in the same generation, but it cannot permanently merge two
conversations. The ledger also retains observed live nicknames so that an old
nickname cannot accidentally reuse the durable JSONL key of a query that has
since changed nickname.

Strong account continuity can rebind a query display nickname without changing
its stable `HistoryConversationKey`. A live Alicia with the same network and
strong account as Alice therefore reuses Alice's conversation. A live Alice
with a conflicting account gets a new isolated conversation key, even if the
original conversation is currently displayed as Alicia. This prevents the
new user's messages from being written into Alice's canonical history file.

If more than one plausible existing query can match, or if accounts conflict,
the policy leaves the candidate ambiguous. The application prefers a bounded
separate recovered/current query over an unsafe merge and never silently
renames an unrelated query.

Account logout does not delete the earlier evidence. A later login with the
same account can establish continuity; a different account is a conflict.
The same account text on another network remains isolated.

The Phase 1Y historical firewall is preserved and strengthened. Historical
JOIN, PART, QUIT, NICK, MODE, TOPIC, KICK, AWAY, ACCOUNT, TAGMSG, and message
events carry server-playback provenance. They may enrich the historical
ledger for the matching private conversation, but they cannot rename the
current query, assign a live account, change current channel members/topic/
modes, or create a current live query merely because a channel playback line
contains an account or nickname.

Reconnect callbacks carry the connection generation. Stale callbacks are
discarded before identity evidence or history projection can reach the
workspace. Live traffic that races recovery remains authoritative for current
ownership; historical traffic remains provenance.

## 4. Anchor/index architecture

`HistoryAnchors.cs` defines the typed local lookup surface:

* `HistoryConversationAddress` contains network, scope, conversation kind,
  display name, and the stable conversation key.
* `HistoryServerMessageAnchorRequest` and
  `HistoryTimestampAnchorRequest` are network/conversation-scoped.
* `HistoryAnchorResult` distinguishes exact, nearest-before, nearest-after,
  nearest-around, and bounded-miss results.
* `HistoryContextRequest` and `HistoryContextResult` describe bounded context
  around a server message ID or timestamp.

The index is the existing JSONL sidecar (`.hidx`), extended to sidecar format
version 2. Each entry stores network ID, timestamp ticks, durable sequence,
source byte offset/length, and an optional bounded server message ID. The
sidecar is disposable and rebuildable. The authoritative source remains the
canonical JSONL event history; the index never changes deduplication,
ordering, or record contents.

The sidecar validates source length and last-write time, entry bounds, UTF-8
message-ID size, exact trailing bytes, and the configured entry limit. A stale,
missing, truncated, malformed, or old-format sidecar is rebuilt. Small files
can be indexed for anchor lookups while existing paged/history reads retain
their existing minimum-build behavior.

The implementation uses a hybrid lifecycle: durable byte offsets make restart
lookups cheap, a bounded in-memory snapshot is cached per active source path,
and the snapshot is discarded on store disposal or source invalidation. No
external database was added and the complete history database is not retained
as application objects.

## 5. Anchor semantics

`NetworkId + ServerMessageId` is the exact lookup identity. A server message
ID from another network cannot collide. Conversation key, scope, kind, and
network are checked again when the indexed JSONL record is materialized.

Timestamp lookup uses canonical timestamp plus durable-sequence ordering:

* at or before chooses the latest eligible timestamp, with the greatest
  canonical durable sequence on an equal timestamp;
* at or after chooses the earliest eligible timestamp, with the smallest
  canonical durable sequence on an equal timestamp;
* around chooses the smallest absolute timestamp distance, breaking ties by
  canonical order toward the earlier record.

Source offset is only a final deterministic tie-break after canonical
ordering. Missing IDs and timestamps return a typed bounded miss. They do not
throw and do not fall back to an unrelated conversation.

The context reader selects a bounded ordered window around the resolved
anchor, materializes only those byte ranges, validates their address and
identity, applies exact canonical deduplication, and returns records in
ascending canonical order. Overlapping local windows therefore do not create
duplicate projected rows. The local window is capped by
`MaximumHistoryContextEntries` on each side and the index candidate set is
capped by `MaximumHistoryIndexEntries`.

The in-memory test store implements the same typed semantics, so application
tests do not depend on file timing to validate ordering and scope isolation.

## 6. Context retrieval and CHATHISTORY

`NetworkSessionManager.LoadContextAroundAsync` now tries exact local history
first. A selected item with a server message ID uses the local msgid anchor;
timestamp-only selections use the timestamp anchor. If the local result is
complete, the manager projects it through the existing dispatcher and sends no
network request.

If local history is absent or incomplete, the existing typed CHATHISTORY path
is used when the negotiated capability and reference type permit it. The
reference is `AROUND msgid=...` or `AROUND timestamp=...`. Servers without
draft history support retain the previous graceful failure/degradation. The
same action remains available through the existing `WorkspaceActionRouter`,
and log-search routing uses a local msgid context when the result has one.

Typed `BETWEEN` request construction already exists from Phase 1Y and remains
available for two exact boundaries. Phase 1Z does not add a synchronization
protocol or automatic exact-gap repair; a full typed gap object is deferred
until both boundaries can be produced naturally by the workspace model.

## 7. Threading and boundedness

Identity observation and query creation run on the existing serialized
workspace mutation path. History completion projects through the existing
dispatcher before touching WPF-bound state. No background index worker writes
to WPF collections. Generation, cancellation, disconnect, and shutdown checks
remain in the session/history lifecycle.

The relevant bounds are:

* 24 identity observations per live or historical query ledger;
* 16 reconnect TARGETS rows;
* 32 automatic reconnect history operations;
* 8 recovered query candidates;
* 200 recovered messages/events;
* 100 context records in each before/after bound;
* 1,000,000 index entries per source;
* existing JSONL record/file and history source-file limits;
* bounded server request sizes and bounded duplicate/result materialization.

Malformed or adversarial input cannot grow identity ledgers, index entries,
recovered candidates, or history context without limit.

## 8. Persistence and schema impact

The durable `ConversationLogRecord` JSONL schema was not changed. Existing
v2 records remain readable, including records with no server message ID. The
`.hidx` sidecar format was versioned from 1 to 2 because server message ID is
now an indexed field; old sidecars are safely rebuilt from JSONL. No history
file is rewritten merely to construct or refresh an index.

Trailing invalid/truncated JSONL records continue to follow the existing safe
reader behavior. Index validation prevents a stale or partially written
sidecar from being treated as canonical history.

## 9. Tests and validation

Focused Phase 1Z application coverage is in:

* `Phase1ZIdentityAndAnchorTests`: typed identity strength/conflict rules,
  network isolation, msgid/timestamp lookup, equal-timestamp tie-breaking,
  missing anchors, restart/rebuild, overlap deduplication, and unchanged
  JSONL contents;
* `Phase1ZIdentityAndContextIntegrationTests`: strong-account nickname
  continuity, conflicting-account safe duplication, and local msgid context
  without CHATHISTORY;
* `Phase1ZAnchorPerformanceTests`: opt-in 12,000-record bounded scenario.

The performance run was enabled with
`NEXIRC_RUN_HISTORY_ANCHOR_PERFORMANCE=1` and reported:

```
history-anchors records=12000 index_entries=1 index_bytes=672032 cold_msgid_ms=374 warm_msgid_ms=0 timestamp_ms=34 context_ms=14 context_records=51 exact=perf-06000 nearest=perf-06000 repeat=perf-06000
```

`index_entries=1` is the number of cached sidecar sources, not the number of
records; the sidecar contained 12,000 bounded entries. Warm and timestamp
lookups use the sidecar rather than rereading the complete JSONL file.

The new deterministic desktop smoke is:

```
dotnet run --project src/nexIRC.Desktop --configuration Release --no-build -- --demo --ui-smoke history-identity
```

It verifies live account evidence, reconnect nickname continuity, conflicting
account separation, exact local msgid context, canonical order, discovery
request draining, typed CHATHISTORY fallback, historical query evidence,
unchanged current channel/query state, no unread changes, and no focus or
notification side effects. The smoke passed and printed:

```
HISTORY_IDENTITY_UI_METRICS queries=3 context=4 account_reuse=true conflict_separate=true historical_firewall=true unread_unchanged=true
PASS_UI_SMOKE history-identity
```

The demo initialization also repaired a pre-existing harness defect: its
in-memory configuration was not loaded before `MainWindow` construction, so
the configured logging preferences were replaced by defaults. Loading the
demo configuration makes the smoke exercise the real JSONL path instead of a
logging-disabled shortcut.

## 10. Regression and live evidence

The full Core suite passed: 80 tests, 0 skipped. The full Networking suite
passed: 42 tests, 0 skipped. The focused retained Phase 1Y/1W Core and
Networking tests passed, as did the focused Phase 1Y/1W/O/U application set
(22 passed, 1 skipped) and all 9 Phase 1Z application tests.

The full Application aggregate completed with 141 passed, 1 skipped, and 1
failed. The failure was the retained Phase 1T accelerated reconnect-retirement
timeout. Phase 1R also failed once in an earlier aggregate run at its initial
15-second convergence wait. Phase 1R then passed twice in isolation with the
unmodified test, and Phase 1T passed in isolation in 23 seconds. The Phase 1R
isolated pass reported the normal `PHASE1R_LONG_SESSION` metrics. The failures
are intermittent endurance timing/aggregate-host conditions rather than
deterministic Phase 1Z assertion failures: all Phase 1Z tests and the new
semantic smoke pass, while the same retained endurance tests pass alone. The
aggregate result is retained as a validation defect and is not concealed.

The Release solution build passed with zero warnings and zero errors before
the final validation pass. Format verification is run as
`dotnet format nexIRC5.sln --verify-no-changes --no-restore`.

The credential-free live IRC smoke used the repository's existing safe Libera
TLS harness and passed. It reported DNS/TCP/TLS, registration `001`, JOIN and
NAMES synchronization, self-WHOIS `318`, clean `QUIT`, and natural window
close. The server advertised `account-notify`, `away-notify`, `batch`,
`extended-join`, `labeled-response`, `message-tags`, `multi-prefix`, and
`server-time`; it did not advertise draft CHATHISTORY or event-playback. No
live history/account correlation claim is made beyond the fields actually
provided by that server.

## 11. Defects repaired and remaining limitations

Repaired defects found during this phase:

1. conflicting live accounts after a nickname change could otherwise reuse a
   former nickname's durable history key; observed live nicknames now force a
   separate key when account evidence conflicts or identity is ambiguous;
2. demo smoke configuration was not loaded, disabling configured logging;
3. local context actions now avoid an unnecessary network request when the
   exact canonical history is already present;
4. historical ACCOUNT playback in a channel no longer creates or mutates a
   current query; matching private historical playback retains provenance in
   the historical identity ledger.

The implementation does not create a globally permanent IRC identity,
perform heuristic identity matching, infer equality from absent accounts, or
automatically repair arbitrary two-ended gaps. Servers that expose only
nicknames remain intentionally ambiguous, and servers without draft
CHATHISTORY continue to provide only local context.

## 12. Outcome and next phase

Outcome A is appropriate for the Phase 1Z objectives: strong identity
evidence and bounded anchor indexing are implemented, integrated,
rebuildable, and validated. The retained aggregate endurance defect remains
an explicit limitation of the validation table, not a reason to misclassify
the identity/index architecture as incomplete. The recommended next phase is to
model an exact bounded gap (`previous anchor`, `next anchor`, network,
conversation, provenance, and local-fill status) only where the existing
workspace can produce both boundaries without guessing, then use it to issue
precise typed BETWEEN requests. That work should preserve this phase's
network isolation, ambiguity policy, canonical source-of-truth rule, and
historical/live firewall.
