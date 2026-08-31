# nexIRC 5 Phase 1H — History Performance, Conversation Navigation, and Activity

## Scope and architecture guardrails

Phase 1H matures the workspace without changing the Phase 1G ownership model:

- Core and application behavior remain WPF-independent; the WPF shell only binds commands and presentation state.
- JSONL remains the authoritative conversation-history format. Existing JSONL files need no migration.
- Closing a view is a presentation action and never sends `PART`.
- `PART`, `PART and close`, rejoin, and historical-view removal are separate operations.
- Stable conversation identity is `(NetworkId, view kind, RFC1459-folded target name)`. Runtime `ViewId` values are presentation handles.
- Drafts are bounded in-memory state, keyed by `(NetworkId, ViewId)`, and never enter logging or history persistence.

## Git and upstream audit

The requested audit was performed before implementation:

- Repository: `D:\dev\nexIRC\nexIRC5`
- Branch: `main`
- Starting HEAD: `ecf34a8924e9447944c32bdc8df9c5becd014b3e`, subject `Implement nexIRC 5 Phase 1G conversation workspace maturity`
- Starting worktree: clean
- Upstream: `origin/main`
- Starting `git rev-list --left-right --count '@{upstream}...HEAD'`: `0 1` (behind 0, ahead 1)
- The parent of Phase 1G HEAD is `cb8f1fb7b3f46727cc6978fdb425f281f7fb205f`, matching the reported Phase 1F ending HEAD.
- After the Phase 1H commit, HEAD is `6dfae919dc88a763786e29c3a2be9887a46da3d1` and the verified ending count is `0 2` (behind 0, ahead 2).

The apparent Phase 1G divergence anomaly is not reproducible as stated when the upstream ref is unchanged. The verified graph is `origin/main` at Phase 1F (`cb8f1fb`) → local Phase 1G (`ecf34a8`) → local Phase 1H (`6dfae91`). Thus Phase 1H started one commit ahead of `origin/main`, and the new Phase 1H commit correctly makes it two commits ahead. If Phase 1G started at its reported Phase 1F HEAD while `origin/main` was unchanged, adding the Phase 1G commit would have changed the ahead count; if it ended one ahead, the upstream ref or starting snapshot must have differed from the report. This is preserved as an audit discrepancy rather than repaired by rewriting history. No fetch, push, or history rewrite was performed.

## History measurement

The opt-in deterministic test is `HistoryPerformanceTests.JsonlHistoryPerformanceReportIsDeterministicWhenExplicitlyEnabled`. It writes deterministic camelCase JSONL records with fixed timestamps and measures newest, oldest, older, newer, around-timestamp/jump-to-date, conversation-scoped search, bounded export preparation, and historical conversation reopen/reuse.

Run it with:

```powershell
$env:NEXIRC_RUN_HISTORY_PERFORMANCE='1'
dotnet test tests/nexIRC.Application.Tests/nexIRC.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~HistoryPerformanceTests.JsonlHistoryPerformanceReportIsDeterministicWhenExplicitlyEnabled" --logger "console;verbosity=detailed"
```

The latest run produced the following machine-dependent timings. Each page operation returned 100 records, search returned 1 result, bounded export returned 500 records, and reopen returned one reused view.

| Records | JSONL bytes | Newest | Oldest | Older | Newer | Around / date | Search | Export | Reopen |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 | 432,890 | 45 ms | 12 ms | 14 ms | 12 ms | 27 ms | 12 ms | 11 ms | 0 ms |
| 10,000 | 4,338,890 | 207 ms | 2 ms | 1 ms | 1 ms | 1 ms | 76 ms | 3 ms | 0 ms |
| 100,000 | 43,488,890 | 1,661 ms | 5 ms | 6 ms | 5 ms | 3 ms | 375 ms | 3 ms | 0 ms |
| 250,000 | 108,888,890 | 925 ms | 11 ms | 20 ms | 11 ms | 6 ms | 716 ms | 3 ms | 0 ms |

The first newest-page read for an indexed dataset includes index construction. Later page navigation and bounded export use the disposable offset index. Search remains linear over the bounded JSONL stream, as intended. Timings are reports, not brittle pass/fail thresholds.

For comparison, the pre-index baseline was measured with the original JSONL scanner. At 1,000 records, representative operations were about 12–54 ms; at 10,000 records, they ranged from about 79–212 ms. The old 16 MiB safety guard made the 100,000- and 250,000-record runs return no records rather than provide meaningful latency data. Phase 1H raises that bounded readable-file ceiling to 256 MiB and adds a smaller acceleration structure for eligible files.

## Indexing decision and semantics — Outcome B

The baseline showed a meaningful navigation problem at realistic larger histories: repeated page/date navigation required rescanning the complete JSONL file, and the old guard prevented meaningful 100k/250k measurements. A database engine is not warranted. Phase 1H therefore adds `JsonlHistoryIndex`, a disposable sidecar index.

The sidecar is `<conversation>.jsonl.hidx` and contains no message text, credentials, or protocol state. Its versioned binary format stores:

- magic and format version;
- source JSONL length and last-write timestamp;
- a bounded list of `(UTC timestamp ticks, byte offset, JSON byte length)` entries.

The index is built only for JSONL files from 1 MiB through 256 MiB and at most 1,000,000 entries. It is scoped to the requested conversation, so duplicate target names on different networks remain isolated by the existing scope path and key. JSONL records are re-read and validated when selected; scope, kind, folded target name, timestamp, record size, and JSON validity must match the index entry.

Missing, corrupt, stale, over-limit, or partially written sidecars are rebuilt. Rebuilds stream the JSONL file, safely skip malformed/oversized records and a truncated final line, verify the source did not change, and atomically replace only the sidecar through a temporary file. If indexing or sidecar writing fails, the existing bounded JSONL scanner remains the fallback. Cleanup invalidates cached indexes and removes sidecars when a JSONL file is deleted or rewritten.

## Conversation navigation

`ConversationNavigationHistory` is a bounded 64-entry identity-only back/forward history. It suppresses adjacent logical duplicates, truncates forward history after a new branch, skips identities whose views are no longer open, and removes identities when a network or historical conversation is removed. It retains no view objects.

The application exposes a stable workspace navigator and a separate `RecentActivity` ordering API. The desktop currently presents the stable workspace order so activity does not silently rearrange the user's mental map. Every item includes the network display name, target, kind, lifecycle state, state marker, and text activity label. Same-name channels and queries on different networks therefore remain unambiguous.

Keyboard/application commands are:

- `Ctrl+Tab` / `Ctrl+Shift+Tab`: next / previous conversation;
- `Ctrl+Alt+PageDown` / `Ctrl+Alt+PageUp`: next / previous unread;
- `Ctrl+Alt+Shift+PageDown` / `Ctrl+Alt+Shift+PageUp`: next / previous highlight or priority;
- `Alt+Left` / `Alt+Right`: conversation back / forward.

The commands route through `NetworkSessionManager`; they are safe with no matches, one view, historical views, disconnected networks, duplicate display names, and closed views. The navigation pane also provides mouse activation.

## Activity, unread, and notification semantics

- Activating/selecting a view clears its live activity state, including reopening and activating a closed logical view.
- Incoming messages in an inactive channel produce unread activity; highlights produce important activity.
- Query/private-message activity is important by the existing policy.
- Self/outgoing echoes do not create unread activity.
- Server/status traffic keeps the existing typed notification behavior and is not converted into an unrelated conversation highlight.
- History paging, around-date navigation, search, and export do not change live activity state.
- Closed, parted, historical-only, and disconnected conversations remain discoverable through lifecycle text and recents; only open views participate in direct unread/highlight navigation.
- The desktop adapter continues to suppress ordinary notifications for the active view and uses the existing typed/coalesced notification service for highlights, private messages, errors, and other configured events.

No per-conversation mute/notification-level preference was added. The existing typed notification preferences and coalescing were already bounded and portable; adding persistence for mute levels would have expanded the phase beyond the higher-priority history/navigation work.

## Lifecycle, close, recovery, and favorites

The context menu distinguishes `PART (keep view)`, `PART and close`, and `Close view without PART`. Rejoin is explicit. Historical-only or parted channel/query views can be removed from the workspace without deleting JSONL history; joined/active conversations cannot be removed through that action. Clear display, history/search, export entry point, favorite/unfavorite, and query close remain available according to target type.

Closing a channel or query records a bounded recent destination. Favorites and recents now show network and lifecycle text (`joined`, `parted`, `disconnected`, `historical`, or `closed view`). `Open selected` reuses/reopens an existing logical view without joining. `Join selected` is a separate explicit action for channel favorites/recents. Reopen/rejoin never creates a duplicate RFC1459-equivalent target.

## Drafts and demo coverage

Drafts remain in memory only, capped at 4,096 characters and keyed by network plus runtime view identity. Switching, keyboard navigation, close/reopen within the process, same-named targets on different networks, and disconnect preserve isolation; removing a network removes its drafts. Drafts are not sent, logged, or serialized.

`--demo` now uses only deterministic fake transports and includes two networks with duplicate `#lounge` and `Mira` targets, live joined channels, a parted channel, a disconnected network, historical channels/queries, closed/reopened targets, unread/highlight activity, favorites, recents, independent drafts, and 5,000 synthetic history records for paging.

## Validation and limitations

The normal suite leaves the largest benchmark opt-in, so routine tests remain bounded. The index test corrupts a sidecar, appends a truncated JSONL tail, and verifies safe rebuild and page correctness. Core, Networking, and Application test results, Release build, format verification, WPF launch/close validation, and live IRC smoke results are recorded in the final Phase 1H report.

The completed validation was:

- Core: 40 passed; Networking: 23 passed; Application: 54 passed; aggregate: 117 passed, 0 failed.
- Release solution build: 0 warnings, 0 errors.
- `dotnet format nexIRC5.sln --verify-no-changes --no-restore`: passed.
- WPF `--demo`: the executable stayed alive after startup, the normal main-window close request succeeded with exit code 0, and no orphan process remained. The available Windows UI helper could not bind the returned window because it reported a stale/mismatched owner after refresh, so no click or keyboard interaction is claimed.
- Live smoke to `irc.libera.chat:6697`: TLS, registration, self-WHOIS, and clean QUIT succeeded with a temporary nickname; `CAP LS 302` was sent in both normal and CAP-first order but no CAP LS response was observed. No channel was joined and no chat message was sent.

Known limitations are intentionally scoped: scoped text search remains a bounded linear scan; files above the 256 MiB readable-history ceiling are not indexed or streamed; the desktop does not add native Windows toast packaging; closed logical conversations are recovered through favorites/recents rather than a separate unbounded closed-tab store; and reliable interactive UI automation/live IRC behavior depends on the host environment.

## Recommended Phase 1I direction

Keep JSONL and the disposable-index fallback contract stable. If real-world traces show search, cross-file search, or very large histories are the next bottleneck, measure those paths separately before considering a coarse search index or archival strategy. A future phase can also add explicit per-conversation notification levels and a richer closed-workspace browser, but should preserve stable identity, bounded state, and the strict Close View != PART rule.
