# nexIRC 5 Phase 1W — Durable Playback Identity, Ordering, and History Continuity

## Outcome and scope

Phase 1W delivers Outcome A: a durable playback contract.  The application now
has explicit provenance, a narrow authoritative identity rule, deterministic
local ordering, backward-compatible JSONL fields, exact server-id duplicate
suppression, and one conservative merge policy shared by persistence, search,
export, and transcript projection.

CHATHISTORY is deliberately deferred.  The selected credential-free Libera
smoke path is suitable for CAP, metadata, registration, and clean shutdown
validation, but this phase does not yet implement a complete bounded
CHATHISTORY request/response lifecycle.  Adding a command before that protocol
contract exists would make old playback look like live state and would make
duplicate handling depend on server-specific batch behavior.

## 1. Existing history architecture before Phase 1W

The pre-Phase-1W path was:

```text
wire line
  -> IrcMessageParser / IrcMessage
  -> ServerSession / SessionStateStore
  -> IrcSemanticEvent
  -> NetworkSessionManager / IrcEventPresentation
  -> WorkspaceView.Entries
  -> ConversationLoggingService.Record
  -> bounded async JSONL writer
  -> page/search/export readers
```

Live entries were projected into `WorkspaceView.Entries` and then logged.  The
writer used a bounded channel and segmented JSONL files.  History navigation
and search read the store, but historical context was projected into a separate
`HistoryContext` collection; it was not merged into the live transcript.  A
loaded record therefore could not be compared with a later server replay using
the same policy.

The store already had useful scale protections: a 2,048-item bounded write
queue, 64 MiB default segments, a 256 MiB readable file ceiling, malformed-line
skipping, a disposable timestamp/offset index, and a disposable search index.
Those structures remain the source of performance behavior; Phase 1W does not
introduce a database or rewrite the history architecture.

## 2. Existing JSONL schema and deficiencies

The legacy record contained `timestamp`, network/profile/scope identifiers,
conversation kind/name/key, sender, message kind, direction, text, and
highlight state.  `ReceivedAt` was intentionally runtime-only and remains
`JsonIgnore`; the displayed timestamp is allowed to be authoritative
server-time.  JSONL uses camel-case properties and string enum converters, and
old numeric enum values remain readable by `System.Text.Json`.

Deficiencies found by the audit:

* timestamp was the only ordering field;
* timestamp was not a message identity;
* there was no persisted server message id;
* there was no persisted local sequence;
* persistence and local-history projection did not share a merge layer;
* search and export sorted primarily by timestamp;
* malformed records were safely skipped, but exact duplicate records had no
  shared server-id policy;
* query conversation keys were stable through an in-session nick rename, but
  that rule was not represented in a playback candidate abstraction.

## 3. Provenance model

`ConversationEntryProvenance` is bounded to:

* `Live` — delivered by the current IRC session;
* `LocalHistory` — read from the local JSONL store for projection;
* `ServerPlayback` — historical content delivered by a future server-history
  operation.

`ConversationEntryCandidate` carries delivery provenance and a `Persist` flag.
Local history is projection-only and is never appended back to the same store.
Server playback is historical for side-effect purposes, but may be persisted
once when it is not already durable.  Provenance does not alter channel/query
identity or ordinary visual rendering.

The durable record also retains the originating provenance for diagnostics and
future recovery.  A runtime candidate can therefore say “loaded locally”
without pretending that a message originally received live was authored by a
different server.

## 4. Authoritative identity and message-id policy

The identity hierarchy is intentionally short:

```text
valid server msgid or draft/msgid
  -> network-scoped exact identity
  -> otherwise no authoritative external identity
```

`IrcMessage.ServerMessageId` accepts the standardized `msgid` tag and the
legacy `draft/msgid` spelling when non-empty and bounded to 256 characters.
All other tags remain neutral metadata.  The id survives the typed message,
`TranscriptEntry`, `ConversationLogRecord`, JSONL, reload, search, and export
paths.

The exact durable duplicate key is:

```text
NetworkId + NUL + ServerMessageId
```

It is not merely the id, and it does not include display sender spelling or
timestamp.  A matching id on another network is a different message.  A local
record sequence is never used as server identity.

When no server id exists, nexIRC does not fabricate one from timestamp, nick,
body, action/notice kind, or any tuple of those fields.  Identical no-id
messages remain distinct, including repeated ACTION and NOTICE/PRIVMSG pairs.

## 5. Durable local sequence

`ConversationLogRecord.DurableSequence` is allocated by the single JSONL
writer, or by the in-memory test store, when a new record has no sequence.  It
is monotonic per `(NetworkId, effective conversation key)`, survives restart by
scanning the bounded set of active/archive segments, and is not global across
networks.  Allocation is serialized by the existing single-reader writer and
the in-memory store lock.

The sequence is an ordering/navigation value only.  It is locally generated,
not authoritative, and cannot make two server observations the same message.
Legacy records with no sequence remain readable and use deterministic fallback
ordering until new records establish sequence values.

## 6. Canonical ordering contract

Conversation ordering is ascending by:

1. displayed `Timestamp` — authoritative server-time when
   `TimestampSource == ServerTime`, otherwise legacy/local receive time;
2. `DurableSequence` for equal timestamps;
3. server identity and bounded sender/kind/text fallback fields for old
   records that have no sequence.

Descending pages/export views reverse this canonical order.  Equal timestamps
   therefore do not depend on filesystem order once records have the new
   sequence.  Clock regressions and older playback are ordinary ordering cases;
   they do not mutate the current session's state in reverse.

Protocol processing order remains separate from conversation display order.
The current `SessionStateStore` applies live JOIN/PART/NICK/presence mutations
in delivery order.  A future playback path must only create historical
conversation candidates and must not feed those messages into participant
lifecycle reconciliation, unread/activity, notifications, or automation.

## 7. Deduplication and merge

`ConversationHistoryMerge` is the shared policy used by stores and search/page
selection.  It removes only exact server-id duplicates within the same
network.  It deliberately does not remove a no-id candidate, even when all
visible fields match another candidate.  This preserves legitimate repeated
messages and avoids the unsafe heuristic:

```text
timestamp + sender + text
```

The merge layer orders the remaining records canonically.  Server-id replay is
an accepted no-op in both JSONL and in-memory stores, so overlapping future
playback windows cannot append the same authoritative record twice.  Local
history reads are not written back, preventing a read/projection feedback loop.

## 8. History schema evolution

The current schema marker is version 2.  Added fields are deliberately small:

* `schemaVersion`;
* `serverMessageId`;
* `durableSequence`;
* `provenance`;
* `timestampSource`;
* bounded `batchId`.

`ReceivedAt` remains runtime-only and is not reintroduced into hot JSONL
records.  Missing fields get safe defaults: current schema version, no server
id, sequence zero, live provenance, local/legacy timestamp source, and no batch.
Existing Phase 1F–1V records therefore remain readable without migration.
Individual malformed or truncated lines remain bounded and are skipped; they
cannot destroy the rest of a file.  Credentials and session secrets are not
added to the schema.  The new fields are bounded and do not include raw IRC
payloads.

## 9. Query identity and nick changes

Channel keys remain RFC1459-folded and network/scope isolated.  A `QueryView`
continues to retain its original `HistoryConversationKey` while an in-session
identity-bound nick changes.  Thus Alice → Alicia keeps one logical query and
older Alice playback can target that key.  The existing conservative boundary
still applies: a same-nick query on another network, a new connection
generation, or an unrelated identity does not prove continuity and is not
merged.

## 10. Playback/live side-effect boundary

The candidate API exposes `TriggerLiveSideEffects`; it is true only for
`Live`.  `ConversationHistoryProjection` converts a durable record into the
same `TranscriptEntry` shape while doing none of the following:

* logging back to JSONL;
* notification publication;
* unread/highlight increment;
* taskbar/activity flashing;
* participant lifecycle mutation;
* contextual action or automation dispatch.

The WPF transcript insertion helper uses canonical ordering and suppresses an
already-present server id without changing activity.  Live routing continues
to use the normal activity/notification path, and live entries carry the same
server id and timestamp-source metadata before they are persisted.

## 11. BATCH integration

Phase 1V's bounded BATCH state remains generation-owned: at most 32 active
batches, bounded ids and parameters, unknown/invalid closures are harmless,
and generation reset clears all active batches.  `IrcMessage.BatchId` now
preserves the message association tag when present, and the typed transcript
and durable record can carry it.

This phase does not treat a historical batch as live session state and does not
implement every BATCH type.  Nested/overlapping ids remain independently
bounded by the existing dictionary; future playback should validate its batch
type and close/timeout behavior before creating candidates.

## 12. Reconnect gap model and pagination

The application contract is the existing bounded `HistoryPageRequest`, which
supports latest, oldest, older (`Before`), newer (`After`), and around-window
reads.  Both local JSONL and future server playback can satisfy that shape.

The reconnect model is:

```text
last live record
  -> disconnect (no synthetic history required)
  -> reconnect
  -> optional ServerPlayback candidates for the gap
  -> new Live records
```

Without server history, behavior remains the existing local append path.  With
server ids, gap filling is exact and idempotent.  Without ids, the contract
preserves observations rather than guessing that a playback item equals a live
item.

## 13. Search and export

Search uses the same exact server-id key, so duplicate authoritative records
appear once while legitimate no-id repeats appear separately.  Results remain
network- and conversation-scoped and retain the authoritative displayed
timestamp.  Export reads the canonical range and orders it with the same
timestamp/sequence policy.  Internal provenance, local sequence, and server id
are not added to ordinary plain-text output; JSONL export retains the durable
fields for round-trip diagnostics.

## 14. Performance and malformed data

The existing disposable sidecar indexes remain reconstructable and timestamp
based; the merge step is linear in the selected bounded window, and duplicate
lookup is a hash-set operation.  No per-record receive-time field was added.
The deterministic volume test inserts 2,000 records and reads a bounded page
of 100 records without an all-history quadratic merge.

Readers skip blank, malformed, oversized, and truncated individual lines.
The JSONL writer keeps the existing queue, segment, and file bounds.  Identity
and sequence indexes are disposable in-memory maps rebuilt from bounded
segments; JSONL remains authoritative.

## 15. Deterministic coverage

`Phase1WHistoryContinuityTests` covers server-id replay, network isolation,
repeated no-id messages, ACTION/NOTICE distinction, equal-time sequence order,
older playback merge, provenance side-effect flags, schema/sequence reload,
search behavior, malformed JSONL, and a deterministic 2,000-record history
volume.  `Phase1WProtocolTests` covers `msgid`, `draft/msgid`, invalid/oversized
ids, and BATCH association preservation.

The retained Phase 1N msgid routing test continues to prove live duplicate
semantic events are projected once and distinct ids remain distinct.  Existing
query identity, generation isolation, network isolation, NAMES, metadata,
transport, shutdown, endurance, and contextual-action regressions remain part
of the full suite.

## 16. Live-server findings and CHATHISTORY gate

The existing live smoke advertises capabilities before registration and uses
the established Libera TLS path.  Phase 1W does not claim CHATHISTORY support
from capability absence, nor fabricate server playback evidence.  The live
server check is supplemental; deterministic tests are authoritative for the
new contract.

CHATHISTORY remains deferred because the client does not yet have all of the
following as one tested slice: capability request policy, bounded command
timeout/error handling, validated history batch decoding, server playback
classification, and a server-specific result-to-conversation mapping that can
be exercised without corrupting current session state.  Phase 1X should add a
minimal `LATEST`/bounded-history operation only after those pieces are
specified and tested against a server that advertises the capability.

## 17. Durable/runtime/authoritative classification

| Value | Durable? | Source | Role |
|---|---:|---|---|
| `Timestamp` | yes | server-time or local fallback | displayed ordering time |
| `ReceivedAt` | no | local runtime | processing diagnostic only |
| `ServerMessageId` | yes when supplied | server | exact identity, network-scoped |
| `DurableSequence` | yes for new records | nexIRC | local equal-time ordering only |
| `Provenance` | yes | delivery path | origin/diagnostics |
| `TimestampSource` | yes | parser/application | authority diagnostic |
| `BatchId` | yes when supplied | IRC metadata | bounded playback diagnostics |
| conversation key | yes | nexIRC identity rules | channel/query identity |

No timestamp/body heuristic is authoritative.  No local sequence is
cross-network identity.  No provenance value is conversation identity.

## 18. Remaining work for Phase 1X

Phase 1X should build on this substrate to add a narrowly scoped, capability-
gated CHATHISTORY operation, batch timeout/result validation, a server playback
request coordinator, and a focused semantic WPF history-continuity smoke.  It
must retain the exact-id/no-id distinction and keep playback outside the live
participant and activity state machine.
