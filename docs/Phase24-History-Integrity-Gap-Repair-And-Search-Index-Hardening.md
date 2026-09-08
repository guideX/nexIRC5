# nexIRC 5 Phase 24

## Outcome

Outcome B. Durable history identity, account-backed exact-gap repair, stronger
search-index source validation, and deterministic WPF accessibility/property
coverage are complete. The available Windows UI Automation bridge could not be
initialized, so this report does not claim real external UI Automation.

The primary acceptance statement is satisfied:

> nexIRC now treats durable conversation identity, not mutable nickname spelling,
> as the authoritative owner of reconnect history, deterministically repairs
> account-backed query gaps across nickname changes, rejects conflicting
> identities, validates search sidecars strongly enough to prevent stale
> canonical anchors, and exposes the resulting history/search navigation surface
> through a verified bounded WPF accessibility path.

## Starting State

- Repository: `D:\dev\nexIRC\nexIRC5`
- Branch: `main`
- Starting HEAD: `1d90e4f901f6232ed9b9d6c9a80bfb786e22b527`
- Starting subject: `Implement nexIRC 5 Phase 23 indexed history search`
- Starting worktree: clean before Phase 24 edits
- Starting divergence: `0 ahead / 0 behind` relative to `origin/main`
- Remote operations: none; no fetch, pull, merge, rebase, push, reset, or discard

The live repository state was authoritative. The Phase 23 baseline was Core
`86/86`, Networking `42/42`, Application `165 passed / 1 skipped / 0 failed`
under the bounded watchdog, a clean Release build, and clean format
verification. Phase 23 retained one isolated Phase 20 account-backed gap
failure.

## Retained Phase 20 Failure

The exact failing test was:

`Phase20ExactGapRepairTests.AccountBackedQueryNickChangeRepairsTheSameDurableQueryAndRejectsConflictingAccount`

Before the production fix, the test reproduced in isolation in roughly one
second. The isolated test and its enclosing fixture were then used to compare
the Phase 1Z identity tests, Phase 20 exact-gap tests, and the WPF
`history-gap-repair` and `history-identity` smokes. The failure was deterministic
for the isolated test, but its cause was a timing/order race in the serialized
projection boundary, not an incorrect expected result and not stale test data.

The old session could still have a semantic callback queued in the dispatcher
after its entry count became observable. Reconnect could capture that old
boundary before the callback reached `SignalReconnectBoundary`. A replacement
`ServerSession` starts at generation 1, so generation alone could not
distinguish the old callback from a valid replacement callback.

The repair records the session instance that owned the reconnect boundary. A
callback from that same old session is ignored when its generation is not newer
than the captured boundary. A replacement session instance, or a genuinely
newer automatic-reconnect generation, can still establish the post-reconnect
boundary. No timeout multiplication or assertion retry was added.

## Durable Identity And Request Ownership

The identity chain is now explicit and consistent:

- The query view keeps one durable key, for example
  `PrivateConversation:alice`.
- The visible nickname may change from `Alice` to `Alicia`.
- Strong account evidence remains `alice123` and is stored in
  `ConversationIdentityEvidence`.
- The current IRC target used on the wire may be `Alicia`.
- The reconnect gap stores the durable conversation key, target, selector
  boundaries, network, and repair generation.
- The repair request carries an immutable `GapKey` in application state. It is
  never serialized onto IRC.
- `HistoryGapPolicy.ValidateBatch` rejects a response whose request gap key,
  durable conversation, target, selectors, batch type, or batch target does not
  match the active gap.

`NetworkSessionManager.HistoryAddress` and `HistoryConversation` are the
central mapping used by logging, pagination, gap repair, search navigation, and
context loading. Current protocol spelling is therefore separate from durable
local ownership instead of being inferred independently by each subsystem.

Once account evidence proves continuity, canonical history continues to use
the durable query key even when the server spelling changes. Old JSONL records
may display `Alice` and later records may display `Alicia`; identity continuity
does not rewrite historical sender text.

### Race handling

- Live nickname-only evidence remains conservative and cannot permanently merge
  an ambiguous user. Later strong account evidence may establish continuity.
- Identity-before-message produces one exact B-to-F gap for the durable query.
- A reused `Alice` with account `other` creates a separate safe query and does
  not add rows or repair requests to Q1.
- A CHATHISTORY response is owned by its immutable request conversation, target,
  selectors, generation, and gap key; it is not routed by the current display
  nickname after completion.
- A second disconnect invalidates the old generation and stale completions are
  rejected before projection.
- Search navigation uses the result's durable key and canonical anchor, so it
  does not create another query or another remote history request while repair
  is converging.

The focused ownership test directly accepts a matching `GapKey` and rejects a
different one. The Phase 20, Phase 1Z, Phase 22, Phase 23, and Phase 24 tests
plus the `history-integrity` smoke cover the combined race behavior.

## Exact Gap Proof

The account-backed scenario is:

1. Q1 receives B as `Alice` with account `alice123`.
2. The session disconnects and a replacement session sees `Alicia` with the
   same account.
3. F arrives under the safe current target `Alicia`.
4. One exact `CHATHISTORY BETWEEN Alicia msgid=b msgid=f` request is created.
5. C, D, and E are persisted into Q1. The duplicate B boundary is not
   projected twice.
6. Search finds D through Q1's canonical key and local navigation activates
   the existing Alicia query without another IRC request.
7. A new `Alice` with account `other` remains isolated.
8. Return to Latest restores live follow state.

The resulting canonical chronology is `B,C,D,E,F` in the focused proof, with
one gap and one repair request. The WPF trace was:

`durable_key=PrivateConversation:alice chronology=B,C,D,E,F between_requests=1 search_d_local=true navigation_same_query=true conflicting_account_isolated=true return_to_latest=true irc_after_repair=0`

## Search Index Integrity

### Prior model and audit

Phase 23 validated `.hsidx` using source length, source mtime, format, sidecar
self-hash, block bounds, continuity, coverage, and Bloom metadata. It did not
bind each indexed range to canonical source content. A same-length replacement
with preserved mtime could therefore look valid and leave offsets pointing into
different JSONL content.

### Chosen stronger identity

The sidecar is now format version 3. Every indexed block stores a SHA-256 source
fingerprint for exactly its canonical byte range. The sidecar still has its
existing payload self-hash. On cold build, all indexed blocks are hashed with a
bounded 64 KiB buffer before the sidecar is written. On sidecar open, every
stored block fingerprint is recomputed and compared in constant time before the
snapshot is accepted.

The stronger identity is deterministic, independent of WPF state, bounded by
the existing block layout, and authoritative only for deciding whether the
disposable accelerator can be used. JSONL remains the source of truth.

### Version and recovery

- Version 2 sidecars are rejected and rebuilt as version 3.
- Truncated, corrupted, missing, and structurally invalid sidecars rebuild from
  canonical JSONL.
- Same-length content replacement with preserved mtime rejects the old sidecar
  and rebuilds it.
- Appending normally preserves incremental indexing. If the final block has
  room, only that affected block is rehashed; if a new block is created, only
  the new block is hashed. Other block fingerprints are retained.
- Canonical JSONL is never changed merely to repair an index.
- The warm cache path checks source and sidecar metadata and uses the already
  validated snapshot; it does not hash the entire history on every search.

The residual limitation is deliberate: an external same-length rewrite that
preserves the source mtime after a snapshot has already entered the same
process cache can evade the cheap warm metadata check until the cache is
invalidated or the store is reopened. Reopen/cold validation catches the
replacement, which is the tested recovery boundary. A future watcher or
periodic bounded revalidation could close that live-process edge without
rehashing every interactive search.

### Same-length replacement proof

The Phase 24 application test and the 50,000-row WPF semantic smoke replace
the canonical marker with an equal-length marker while restoring the original
mtime. The old marker returns zero results, the replacement marker returns one
result, the sidecar reports a rebuild, no stale offset leaks, and canonical
JSONL length remains unchanged.

The smoke trace was:

`rows=50000 cold_build_ms=1387 replacement_rebuild_ms=458 warm_new_ms=19 old_results=0 new_results=1 preserved_mtime=true canonical_length_unchanged=true`

## WPF Accessibility And Navigation

`LogViewerWindow` now exposes standard WPF automation names and stable IDs for
the history search window, query/scope/network/conversation controls, UTC
inputs, msgid input, navigation buttons, result list, and result items. The
interactive surface has an explicit tab order. Enter submits the search from
the query field and opens the selected result from the result list. Escape
cancels an active search or closes the window. Invalid UTC/msgid input returns
focus to the editable field; opening a result returns focus to the result list.

The deterministic `accessibility` smoke instantiates the real WPF history
window and verifies window/control names and IDs, tab indices, a named result
list item, and invalid-msgid focus. Its trace was:

`automation_ids=true tab_order=true result_item_named=true invalid_msgid_focus=true deterministic_wpf_properties=true`

The smoke does not pretend to be external UI Automation. The attempted
Windows bridge initialization failed with:

`Trusted RPC service is not configured: sky`

The repository therefore has executable WPF property/command coverage, but no
claim of UIA tree discovery or HWND-driven clicking. Go to Msgid, Go to UTC
Time, Load Older, Load Newer, and Return to Latest remain covered by the
existing semantic navigation paths and focused validation tests; failed input
focus is covered directly by the new WPF smoke.

## Validation

### Focused and aggregate tests

- Phase 24 focused tests: `3 passed / 0 skipped / 0 failed`.
- Core: `86 passed / 0 skipped / 0 failed`.
- Networking: `42 passed / 0 skipped / 0 failed`.
- Final full Application suite: `168 passed / 1 skipped / 0 failed` in `1 m 52 s`.
- The one skip is the existing opt-in Phase 1O benchmark.
- Three whole-host runs after the Phase 20 repair produced two clean
  `167/1/0` runs and one unrelated Phase 1M aggregate failure. The failing
  Phase 1M test passed in isolation (`1/1`) and its enclosing fixture passed
  (`2/2`). The latest full run, including the Phase 24 ownership test, was
  clean at `168/1/0`.
- The full Application suite covers the Phase 1R through Phase 1Z, Phase 20,
  Phase 21, Phase 22, and Phase 23 test fixtures. The focused Phase 20 exact-gap
  run was `4/4` before the final aggregate run; the Phase 24 focused run is
  `3/3`.

### Semantic WPF smokes

The required history and scheduling smokes passed:

- `chathistory`
- `history-discovery`
- `history-identity`
- `history-gap-repair`
- `history-pagination`
- `history-navigation`
- `history-search`
- `history-integrity`
- `stale-search`
- `index-recovery`
- `index-fingerprint`
- `accessibility`
- `sustained-interactivity`
- `burst-fairness`

The history integrity, index fingerprint, accessibility, sustained
interactivity, and fairness smokes were rerun individually after their final
changes. Sustained interactivity finished with `11/11` active overlaps and
cooperative scheduling metrics; fairness preserved ordered tails under a
5,000-event burst with `selection_before_burst_complete=true`.

### Performance

The existing 50,000-record indexed search performance test passed. Its final
measured values were approximately:

- Phase 23 baseline: cold build `1722 ms`, warm rare search `115 ms`.
- Phase 24 full performance test: cold build `2145 ms`, warm rare search
  `131 ms`.
- Phase 24 50,000-row fingerprint smoke: cold build `1387 ms`, replacement
  rebuild `458 ms`, warm replacement search `19 ms`.

The modest cold-build increase is the expected cost of block SHA-256 source
validation. Warm interaction remains bounded and indexed. Existing Phase 23
tests cover incremental append; the implementation rehashes only the affected
final/new block metadata.

### Build and format

- Release solution build: passed with `0 warnings / 0 errors`.
- `dotnet format nexIRC5.sln --verify-no-changes --no-restore`: passed.
- `git diff --check`: passed.

### Live IRC

The credential-free Libera smoke passed over real TLS. It validated DNS/TCP/TLS,
CAP, registration `001`, JOIN/NAMES synchronization for `#libera`, account and
server-time observations, self-WHOIS through numeric `318`, and clean QUIT and
disconnect. The server advertised account-notify, extended-join, multi-prefix,
server-time, and related capabilities, but did not advertise CHATHISTORY. No
live exact-gap behavior is claimed.

## Defects And Repairs

- Repaired the retained account-backed gap race by fencing callbacks by owning
  session instance and reconnect generation.
- Added immutable `GapKey` request ownership and exact response validation.
- Added versioned per-block source fingerprints to `.hsidx`.
- Preserved incremental index extension and made warm validation metadata-only
  after validated cache admission.
- Added same-length/preserved-mtime, old-version, and immutable-source tests.
- Added deterministic history-integrity, 50,000-row index-fingerprint, and WPF
  accessibility smokes.
- Added keyboard activation, Escape handling, stable automation properties,
  result-item names, explicit tab order, and invalid-input focus behavior.
- Replaced the sustained-smoke implicit overlap assumption with an explicit
  first-batch release gate. This is coordination, not a timeout or sleep.

## Remaining Limitations

- External Windows UI Automation could not run because the `sky` trusted RPC
  service was unavailable. Deterministic in-process WPF property/command
  validation is the reported accessibility result.
- A same-process external rewrite that preserves both content length and mtime
  after warm cache admission remains a residual cache invalidation edge.
- Libera does not advertise CHATHISTORY in the live smoke, so live exact-gap
  behavior remains covered only by deterministic fake transports.
- One unrelated Phase 1M aggregate run was order/host-sensitive even though
  isolated and fixture runs passed. The latest complete Application run is
  clean.

## Recommendation For Phase 25

Keep durable conversation identity and canonical history ownership as the
prerequisite for any message-level relationships. A useful next step is a
small cache-invalidation design for externally modified JSONL, followed by
real UIA bridge repair and a focused race-matrix fixture for live-message-first,
second-disconnect, and search-during-repair cases. Do not add replies, edits,
reactions, or richer relationships until those ownership and validation edges
are covered without relying on nickname spelling.
