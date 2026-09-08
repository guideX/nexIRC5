# nexIRC 5 Phase 20 — Exact Gap Repair and Bounded History Synchronization

## Scope and starting state

Phase 20 closes the reconnect-history gap between the last canonical message
before disconnect and the first canonical message after reconnect. The repair
is session-owned and bounded; JSONL conversation history and its `.hidx`
metadata remain the durable source of truth.

The implementation was made on `main` from commit
`c62839f7c68fa74a46858cbf9781bb5ee0f89108` (`Implement nexIRC 5 Phase 1Z
identity evidence and history indexing`). The observed starting divergence was
`origin/main...HEAD = 0 0`; the requested “one commit ahead” condition was not
present in the checkout. No remote operation was performed.

The wire decisions follow the current IRCv3 CHATHISTORY draft: `BETWEEN` uses
two selectors, excludes both boundary selectors, and does not define a useful
ordering guarantee for a response page. See the
[IRCv3 CHATHISTORY specification](https://ircv3.net/specs/extensions/chathistory.html).

## Exact gap model

`HistoryGapBoundary` records:

- network identity;
- durable conversation identity (`Channel:#name` or the stable query key);
- typed `msgid=` or `timestamp=` selector;
- canonical timestamp and timestamp provenance;
- server message id when present;
- local durable sequence when available;
- boundary provenance and connection generation;
- canonical/stale status and derived trust (`ExactServerMessageId`,
  `TimestampBounded`, or `Untrusted`).

`HistoryGap` stores the older and newer boundaries, the current wire target,
repair generation, lifecycle state, rounds, recovered count, and the last
diagnostic reason. `HistoryGapLedger` is runtime/session-owned and is not
serialized into a second history database.

The exact eligibility policy rejects:

- cross-network or cross-conversation boundaries;
- stale or mismatched connection generations;
- wildcard, mixed-reference, unsupported, or selector/canonical-id mismatch;
- timestamp selectors without server-time evidence;
- reversed, equal, or otherwise non-advancing canonical order;
- equal-time boundaries without deterministic durable sequence evidence.

Opaque msgids are never ordered lexically. When timestamps tie, the local
durable sequence is the only accepted tie-breaker.

## Reconnect detection and identity safety

On disconnect/reconnect waiting, the manager captures the newest canonical
boundary for each joined channel and each open, populated query. It then waits
for the first non-historical message routed to that same durable conversation
in the new generation.

For channels, the current channel target is used. For queries, routing and
stable account-backed identity are checked before the live message is accepted
as the newer boundary; a nickname spelling alone cannot merge conflicting
identities. `TARGETS` remains discovery evidence and fallback support, not a
proof of query continuity or a BETWEEN boundary.

If the newer boundary is not observed in the bounded observation window, the
pre-existing bounded `LATEST`/`TARGETS` recovery path remains available. If
CHATHISTORY is not usable, the manager downgrades to that existing fallback
without fabricating an exact gap.

## Exact request lifecycle

The new typed factory emits the precise form:

```text
CHATHISTORY BETWEEN <target> <older-selector> <newer-selector> <limit>
```

For the msgid case, for example:

```text
CHATHISTORY BETWEEN #room msgid=older msgid=newer 50
```

The request uses the existing one-active-request-per-session tracker,
connection-generation fence, timeout, cancellation, and batch correlation.
The result now retains batch id/type/target and typed end-of-history evidence.

The response validator requires the expected `chathistory` batch, request
identity, network, durable conversation, and target. It classifies:

- interior historical rows;
- exact boundary duplicates;
- explicitly tagged context rows;
- unrelated rows;
- malformed rows.

Unrelated or malformed content fails closed. Exact server-id duplicates are
left to the canonical projection/dedupe pipeline; no content, sender, or
timestamp heuristic deletes repeated no-id messages. Exact msgid repairs can
accept interior rows without server-time when those rows carry their own
server msgid; timestamp-bounded repairs require server-time rows.

## Completion and pagination safety

An exact repair reaches `Repaired` only when the interval is empty or the
opening history batch supplies explicit `draft/chathistory-end` evidence. A
short page is never treated as end-of-history merely because it contains fewer
than the requested limit.

Because CHATHISTORY response ordering is not a safe implicit cursor, a
non-empty response with no explicit end evidence is projected canonically but
then marked `Exhausted` with a diagnostic instead of repeating the same
BETWEEN request. This prevents a duplicate-only response or an unordered page
from causing a busy loop or silently skipping unseen history. A future safe
pagination cursor/label can extend this state machine without changing the
boundary model.

The ledger defaults are deliberately finite:

| Budget | Default |
| --- | ---: |
| Active exact repairs | 1 |
| Rounds per gap | 4 |
| Entries per gap | 200 |
| Operations per reconnect | 32 |
| Outstanding gaps | 32 |
| Gap lifetime | 5 minutes |

## Local-first indexed recovery

Before sending BETWEEN, the manager queries `ReadContextAroundAsync` through
the existing JSONL plus `.hidx` anchor layer. A local repair is accepted only
when the bounded indexed context contains both anchors and a non-empty interior
made of persisted server-playback rows with batch provenance. Merely seeing
the two live anchors is not treated as proof that the server-side interval was
filled. Timestamp-only local lookup is allowed only for server-time boundaries.

If local evidence is insufficient, the exact network request is issued. This
keeps local history authoritative while avoiding a false “already repaired”
result for a reconnect that has only recorded the two endpoints.

## Canonical convergence and firewall behavior

All repaired rows enter the existing canonical transcript path. The path:

- orders by authoritative server timestamp, durable sequence, and existing
  deterministic fallbacks;
- deduplicates exact server message ids only within the network/conversation
  identity scope;
- retains repeated no-id messages as distinct rows;
- preserves the durable query key across nickname changes;
- prevents historical JOIN/PART/QUIT/NICK/MODE/TOPIC/ACCOUNT/AWAY/KICK and
  related state events from mutating present membership, topic, modes,
  identity, unread counts, or live activity.

## Verification

Focused Phase 20 coverage includes typed BETWEEN wire construction, end-tag
recognition, mixed-reference policy separation, unsafe boundary rejection,
duplicate/budget termination, channel exact repair, query nickname-change
repair, and conflicting-account isolation.

The deterministic WPF harness includes `history-gap-repair` and reports:

```text
HISTORY_GAP_REPAIR_UI_TRACE state=repaired between_requests=1 chronology=A,B,C,D,E,F unread_unchanged=true
PASS_UI_SMOKE history-gap-repair
```

The neighboring `chathistory`, `history-discovery`, and `history-identity`
smokes also pass. The repository live harness completed against Libera with
DNS/TCP/TLS, registration, `#libera` join/NAMES synchronization, server-time
observation, WHOIS 318, and clean QUIT/disconnect.

Phase 1T endurance was run independently: 2 tests passed. The Phase 1Z
anchor performance scenario remains the indexed lookup guardrail and is run as
part of the final verification command set.

## Known limitation and next recommendation

This phase intentionally chooses bounded correctness over speculative
pagination. If a server omits both explicit end evidence and a safe cursor,
the valid page is retained but the gap is reported as exhausted/unresolved.
The next protocol improvement should be a tested server-supported pagination
cursor or label that can prove progress between BETWEEN rounds; no client-side
inference from response length or opaque msgid ordering should replace it.
