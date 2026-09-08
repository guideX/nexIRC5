# nexIRC 5 — Phase 23

## Indexed history search, canonical result navigation, strict jumps, and aggregate endurance

Phase 23 extends the existing conversation-log search and Phase 22 history
navigation paths. Canonical JSONL remains authoritative; the new .hsidx file
is disposable acceleration metadata. Search results carry a network-scoped
durable conversation identity and, for JSONL results, a validated source
anchor that can be handed directly to Phase 22 navigation.

The primary acceptance statement is satisfied: nexIRC can search durable IRC
history through a bounded rebuildable index, return network- and
conversation-safe canonical results, and navigate directly to the correct
historical message without requiring that message to be in the 500-row WPF
transcript or requiring new IRC traffic when the canonical row is local.

## Repository and preflight

- Repository: D:\dev\nexIRC\nexIRC5
- Branch: main
- Starting HEAD: 00e41144b20eb8e333bfe2ec8af2129414591ba6
- Starting subject: Implement nexIRC 5 Phase 22 bidirectional history navigation
- Starting worktree: clean
- Live starting divergence: 0 ahead / 0 behind
- Remote: git@github.com:guideX/nexIRC5.git

The Phase 22 report said its ending state was 0 ahead / 1 behind, which is
unusual alongside its report of no remote operations. Live inspection showed
local main and origin/main at the same commit and
git rev-list --left-right --count HEAD...origin/main reported 0 0. The prior
report was therefore stale or another process updated the remote-tracking ref
before this phase. No fetch, pull, merge, rebase, reset, or other
reconciliation was done.

## Existing-search audit

The pre-existing implementation was extended rather than replaced:

- ConversationLogQuery, ConversationLogSearchResult, and
  JsonlConversationLogStore.SearchDetailedAsync were the existing search
  abstraction.
- LogViewerWindow was the existing WPF search/history surface.
- Search already supported current-conversation, current-network, and all
  history scopes plus sender, event-kind, and date constraints.
- JSONL was already the durable source of truth and Phase 22 already had
  .hidx anchor metadata and bounded context navigation.
- The important gap was that search returned a row-shaped result and used a
  coarse JSONL scan path; selection manually reconstructed context instead of
  delegating to Phase 22.
- The explicit Search button remains the debounce policy. Previous work is
  cancelled, and this phase adds a monotonic generation fence so a late
  completion cannot replace newer results.

## Search and result architecture

ConversationLogSearchResult now exposes typed identity and display fields:

- NetworkId, ScopeId, optional ProfileId
- durable ConversationKind, display ConversationName, and
  DurableConversationKey
- canonical Timestamp, DurableSequence, and ServerMessageId
- Sender, MessageKind/EventType, bounded Preview/Snippet
- Location plus CanonicalAnchor when the result came from JSONL

The result never depends on a WPF row index, tab index, nickname spelling
alone, or transient view reference. A JSONL location includes source path,
byte offset, byte length, network, conversation, timestamp, sequence, and
msgid metadata. The source path is an opaque local hint, not a second source
of truth.

Search scopes are explicit:

- Current conversation requires network, durable scope, conversation kind,
  and conversation name. An invalid request returns a validation error and
  does not broaden to all history.
- Current network requires a network identity.
- All history may search stored scopes, but each result retains its originating
  NetworkId and durable conversation key.

Free text is a case-insensitive ordinal substring over message/event text,
sender text, and conversation display name. There is no stemming, fuzzy
matching, semantic search, embedding search, or natural-language parsing.
The sender filter uses RFC1459 casemapping. The message body is not folded
with IRC nick casemapping. The Bloom accelerator indexes ASCII case-folded
trigrams; non-ASCII terms conservatively bypass the Bloom filter so Unicode
search cannot produce a false negative. Punctuation remains part of ordinary
substring matching.

Dates are compared as UTC timestamps. From and To are inclusive and
normalized to UTC. Results are deterministic newest-first, ordered by the
existing canonical timestamp/sequence/source ordering, never by filesystem
enumeration or dictionary order. The application limit is 500 results; the
UI requests that limit and reports result/file truncation. Snippets are
bounded to 240 characters. Query text is limited to 256 characters, sender
filters to 128, conversation labels to 256, durable keys to 512, and msgids
to 256.

## Disposable .hsidx accelerator

The index is a compact coarse block accelerator, not a duplicate message
database:

- magic NEXSIDX1, format version 2
- one block per at most 4,096 records
- 128-byte Bloom filter per block, covering trigrams from text, sender, and
  conversation name
- timestamp minimum/maximum, source byte offset/length, and record count per
  block
- at most 4,096 blocks per source and a 256 MiB canonical source bound
- building starts at the existing 1 MiB source-size threshold
- SHA-256 hash over the sidecar payload

Search reads matching blocks and materializes candidate records from canonical
JSONL. A rare term therefore skips old blocks; a broad term may still examine
all matching blocks. The index stores no full canonical messages and cannot
replace JSONL.

When a canonical record is appended, the writer first persists the JSONL row.
If the current sidecar describes the immediately preceding source length, it
extends the last block or creates the next block and atomically rewrites the
sidecar. Segment rotation invalidates the old sidecar and lets the next
search rebuild it. Any sidecar write, validation, or append failure is best
effort: canonical persistence still succeeds, the cached sidecar is
discarded, and a later search rebuilds from JSONL.

Sidecar loading validates source existence, source length and UTC last-write
ticks, format/version, payload hash, block limits, block continuity, and final
source coverage. Incompatible, corrupt, truncated, deleted, or stale
sidecars are ignored and rebuilt conservatively. Retention deletion and
source rewrite paths invalidate both .hidx and .hsidx. The implementation
deliberately does not claim a full content fingerprint from length and mtime
alone; every canonical result anchor is revalidated against the source
record's network, scope, conversation, timestamp, sequence, text, and msgid
before navigation.

The existing truncated-trailing-JSONL tolerance is preserved: malformed or
partial lines are skipped safely by the canonical reader, and a rebuild uses
only readable records. Search-index metadata can always be discarded without
altering canonical history.

## Canonical navigation and identity continuity

NetworkSessionManager.NavigateToHistorySearchResultAsync validates the
result's network, scope, kind, and durable conversation key against the
selected view, constructs a HistoryNavigationRequest, and calls the existing
Phase 22 NavigateHistoryAsync implementation. There is no second
search-result context loader.

Local navigation attempts the canonical source anchor first. If it is missing
or fails validation, existing msgid or timestamp lookup is used. Only a safe
local miss can proceed to Phase 22's bounded CHATHISTORY AROUND fallback;
search never performs speculative remote text search or downloads arbitrary
server history. Local results require no IRC request.

The normal navigation window is 50 records before and 50 after the anchor.
The transcript projection remains bounded at 500 rows. No-msgid rows remain
distinct through durable sequence and source offset; they are not collapsed
into a content hash. Server-msgid deduplication is scoped by network, so the
same msgid on two networks remains two results.

Durable query keys are preferred over mutable nicknames. A historical Alice
result can open a query whose current display nickname is Alicia when both
carry the same durable key. Conflicting account evidence continues to use
the existing identity firewall and separate query path. Historical JOIN,
PART, QUIT, NICK, MODE, TOPIC, and other event rows may be searched and
navigated, but the existing event-playback firewall prevents historical rows
from mutating current channel state.

## Strict user input and WPF integration

The existing modest LogViewerWindow now includes:

- strict UTC From/To fields and a Go to UTC time action
- a Go to msgid field/action
- existing scope, network, conversation, sender, event-kind, result-list,
  cancel, open, paging, and export controls
- result display containing timestamp, network, conversation, sender,
  bounded snippet, and event type for non-normal messages

Msgids are trimmed, bounded opaque tokens. Empty, whitespace-containing, or
control-containing values are rejected; they are never parsed numerically.
The UTC parser accepts invariant ISO forms with optional fractional seconds
and rejects culture-local ambiguous forms such as 09/07/2026 12:30. Explicit
UTC mode avoids DST ambiguity and skipped-local-time behavior.

Every new search cancels the previous linked token and receives a new
HistorySearchGeneration. A completion may publish only while both its
generation and cancellation-source reference remain current. Closing the
window cancels the lifetime and invalidates the generation before disposal.
Search workers do not mutate WPF collections; result publication resumes at
the WPF boundary. Network/conversation identity is rechecked at activation.

## Performance and correctness evidence

The substantial fixture contains 50,000 canonical rows, two network IDs, four
conversation files, repeated common terms, one rare term, equal timestamp
clusters, and rows both with and without msgids. Measured output:

phase23-performance records=50000 files=4 coldBuild=1722ms warmRare=115ms commonExamined=50000 rareExamined=4096 rareSkipped=45904 filtered=25

The common query returned its bounded 100-row test page and reported
truncation. The rare query returned one exact result, used all four indexes,
examined one 4,096-row block rather than all 50,000 rows, and skipped 45,904
rows by index. The sender/date-filtered query returned 25 Alice rows. The
focused canonical-navigation test found a row outside the projected window,
loaded bounded context, selected the exact anchor, and issued zero fake IRC
requests.

Focused Phase 23 coverage includes strict msgid/UTC input, invalid scope
rejection, snippet and result bounds, incremental append to a warm index,
conversation-label matching, cross-network duplicate text, same-msgid
collision isolation, repeated no-msgid identity, stale completion fencing,
and result-to-Phase-22 navigation from a trimmed transcript.

## Aggregate Application-host investigation

The pre-change bounded Application aggregate baseline completed in about
1m45s with 156 passed, 1 skipped, and one failed existing Phase 20 test:

Phase20ExactGapRepairTests.AccountBackedQueryNickChangeRepairsTheSameDurableQueryAndRejectsConflictingAccount

Its diagnostic expected CHATHISTORY BETWEEN Alicia msgid=b msgid=f, but
observed output contained only CAP registration and CHATHISTORY TARGETS; the
Alicia query existed while the gap state remained empty. The test also failed
when run in isolation.

A later unbounded aggregate attempt reproduced that assertion failure and
continued beyond the prior practical runtime, so it was stopped rather than
left running indefinitely. Seven child processes belonging to that exact run
were stopped explicitly; no repository files were removed or reset. A fresh
bounded watchdog run using a 30-second per-test hang diagnostic completed in
2m03s with 165 passed, 1 skipped, and 0 failed, with no hang dump or sequence
file.

The evidence does not justify a production lifetime change: the aggregate
watchdog run did not reproduce a whole-host stall, while the retained Phase
20 failure is a narrower query-gap/account-evidence synchronization problem.
No assertion was weakened and no arbitrary sleep or timeout inflation was
added. The Phase 20 issue remains a documented limitation rather than a
Phase 23 search/index defect.

## Smoke and regression validation

The new desktop semantic smoke scenarios use the existing demo harness:

dotnet run --project src\nexIRC.Desktop -c Release --no-build -- --demo --ui-smoke history-search
dotnet run --project src\nexIRC.Desktop -c Release --no-build -- --demo --ui-smoke stale-search
dotnet run --project src\nexIRC.Desktop -c Release --no-build -- --demo --ui-smoke index-recovery

Observed results:

- history-search passed common-term bounding, cross-network duplicate
  isolation, durable Alice/Alicia query continuity, canonical trimmed-window
  navigation, event firewalling, strict msgid/time service paths, zero local
  IRC requests, and Return to Latest.
- stale-search passed generation ownership and close invalidation.
- index-recovery deleted one sidecar, reopened a fresh store, rebuilt one
  index from unchanged canonical JSONL, and passed source-length invariants.

The existing history-navigation, history-identity, history-gap-repair,
history-pagination, forward-pagination, and history-discovery desktop smokes
also passed. Their traces confirmed local msgid/time jumps, earlier-timestamp
tie breaking, exact gap repair, bounded backward/forward paging, durable
query identity, historical event firewalling, and unchanged live state.

Full Core and Networking suites passed. The final Application suite was run
with a bounded watchdog and completed at 165 passed, 1 skipped, and 0 failed.
The 50k performance test is opt-in by category and passed. Release
solution build passed with zero warnings and errors, and repository-native
dotnet format nexIRC5.sln --verify-no-changes --no-restore passed.

The credential-free Libera smoke passed with DNS/TCP/TLS, registration 001,
JOIN/NAMES synchronization, server-time, account/away/batch/extended-join/
message-tags/multi-prefix capabilities, self-WHOIS through numeric 318, and
clean QUIT/disconnect. It did not use server-side CHATHISTORY to manufacture
search fixtures.

The in-process WPF harness intentionally exercises the same view-model,
session, JSONL, and dispatcher paths without HWND automation. It validates
semantic state and zero-traffic behavior, not physical-pixel rendering or
hardware input latency. Direct control-click automation for every dialog
field remains outside this repository's smoke harness.

## Defects, repairs, outcome, and Phase 24

Phase 23 repaired the missing canonical result seam, added strict shared
input validation, added source-validated anchor navigation, made the search
index incremental and disposable, invalidated stale index metadata during
retention/rotation, fenced stale WPF completions, and preserved durable query
identity during result activation. The index-recovery smoke initially exposed
that the demo's 32 KiB segment size is below the 1 MiB indexing threshold;
the smoke was corrected to use a deterministic >1 MiB fixture and a fresh
store restart, leaving production thresholds unchanged.

Outcome A: bounded indexed local history search, canonical result identity,
direct Phase 22 navigation, strict msgid/UTC input, incremental recovery, WPF
semantic integration, and aggregate behavior are implemented and
validated/classified. The remaining Phase 20 failure is unrelated to the
Phase 23 search/index path and was not hidden.

Intentional limitations are no full content fingerprint in .hsidx,
non-ASCII terms bypass the Bloom accelerator, no remote text search, and no
physical HWND/pixel automation in the semantic smoke. Recommended Phase 24:
repair the Phase 20 account-backed nickname-change/gap-repair synchronization
failure, consider a stronger canonical-source generation/fingerprint for
external log editing, and optionally add accessibility-level control
automation for the UTC/msgid fields.
