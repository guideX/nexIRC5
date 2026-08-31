# nexIRC 5 Phase 1J — Segmented History, Archival, Retention, and Scale Readiness

## Result

Phase 1J reached **Outcome A — filesystem segmentation is sufficient** for the
measured workloads. The authoritative JSONL format remains appropriate when a
logical conversation is spread over bounded physical files. Deterministic
filesystem discovery, per-segment `.hidx` navigation sidecars, and per-segment
`.hsidx` search sidecars kept paging, timestamp navigation, selective search,
and retention operational without a durable manifest or aggregate catalog.

The measured boundary is clear: cold index construction and common/broad
searches remain linear, and very large history still pays that cost. This is
predictable and visible in the metrics below. No database dependency is
justified by Phase 1J evidence.

## Starting repository state

- Repository: `D:\dev\nexIRC\nexIRC5`
- Upstream: `git@github.com:guideX/nexIRC5.git`
- Branch: `main`
- Starting HEAD: `5dde3df4e52107e21c19a79ce119d887cc3432c6`
- Starting subject: `Implement nexIRC 5 Phase 1I scalable history search`
- Starting worktree: clean
- Starting ahead/behind: `0/0` against `origin/main`
- Fetch, pull, merge, rebase, and push: not performed

The final commit and ending worktree state are recorded in the final report
for this phase. The documentation was authored before that local commit so the
commit contains the evidence it describes.

## Phase 1I audit

`IConversationLogStore` exposes append, logical page windows, bounded ranges
for export, basic and detailed search, cleanup, and flush. The JSONL provider
uses one history scope directory per network profile/scope and a shortened
SHA-256 of the RFC1459-folded conversation key for the filename. This keeps
duplicate channel names on different networks separate and also covers
channels, private/query conversations, and status conversations without
putting user-controlled names directly in filenames.

The existing `.hidx` sidecar is a disposable timestamp/byte-offset/length
index. It accelerates newest/oldest paging, date navigation, and bounded
export. The existing `.hsidx` sidecar is a disposable block metadata/Bloom
accelerator containing no message text. JSONL remains authoritative for every
read and search. Missing, stale, truncated, structurally invalid, checksum
invalid, or same-size damaged sidecars are ignored and rebuilt or bypassed.

The Phase 1I bounded rules remain in force:

- 100 records per history page;
- 10,000 records per bounded range/export;
- 1 MiB maximum export file;
- 256-character search text;
- bounded search result collection with a maximum of 500 returned results;
- 256 MiB maximum readable/writable JSONL source;
- 4,096-character in-memory drafts;
- disposable indexes never contain drafts or protocol/session state.

The Phase 1I source-oriented search abstraction was retained. Its typed result
location still carries network/scope identity, conversation key, timestamp,
and optional source offset/length, allowing an archived result to be routed to
the existing historical-conversation workflow.

## Physical segment model

A logical conversation has one active compatibility filename and zero or more
closed siblings:

```text
<root>/<history-scope-id>/<conversation-hash>.jsonl
<root>/<history-scope-id>/<conversation-hash>.s00000000.jsonl
<root>/<history-scope-id>/<conversation-hash>.s00000001.jsonl
...
```

The unsuffixed `.jsonl` file is always the active append target. The numeric
`.s########.jsonl` files are ordered archived segments; the active file is
logically newest even though it sorts after the numeric archive names. The
conversation hash continues to be derived from the full logical key, so
network-qualified scope identity remains outside the display name and two
`#lounge` conversations on different networks cannot collide.

The filename is only a physical lookup key. The record remains self-describing
with network ID, scope/profile ID, conversation kind/name, conversation key,
timestamp, sender, event kind, direction, and text. No durable manifest was
introduced.

## Rotation behavior

`JsonlConversationLogStore` accepts a configurable maximum segment size. The
default is 64 MiB, below the 256 MiB hard safety boundary; tests and demo mode
use smaller thresholds. Before an append, the single writer checks both the
hard source limit and the configured segment threshold. If the active file is
non-empty and the next complete JSONL record would cross the configured
threshold, the writer:

1. flushes and closes the active file;
2. removes only its disposable sidecars and in-memory sidecar cache entries;
3. moves it to the next unused deterministic `.s########.jsonl` name;
4. creates an empty unsuffixed active file; and
5. appends the record to that new active file.

A single record larger than the configured threshold is permitted as its own
segment, subject to the existing record and 256 MiB limits. A failed move or
append completes the pending write as false and leaves the authoritative file
data intact; if the move succeeded but creating the empty active file fails,
the next append recreates the active path. Queue cancellation is observed
before enqueueing, and shutdown drains the single writer queue.

The rotation sequence is discovered from existing sibling names, so repeated
rotations remain deterministic after restart. Closed segments are never append
targets. Retention may rewrite a segment when its cutoff intersects that
segment, which is the intentional maintenance exception to append immutability.

## Logical history APIs

Paging and range reads now discover all segments for the requested logical
conversation, read at most the bounded per-segment candidate page/range, and
merge/sort records by timestamp using a globally bounded page candidate window.
This covers:

- newest page over active and archived sources;
- backward and forward page windows across boundaries;
- oldest-page reads;
- timestamp/around navigation;
- bounded date-filtered export and JSONL export preparation; and
- archived search-result navigation through the existing network-qualified
  historical-view route.

An empty active file created after rotation is harmless and is not returned as
a page source. A missing segment discovered during concurrent inspection is
skipped safely. A missing or corrupt sidecar falls back to authoritative
JSONL. No operation loads an entire segment solely to rotate, index, or
retain it; page candidates and search results are bounded.

## Segment discovery and manifest decision

Discovery is ordinary bounded filesystem enumeration. For a known conversation,
the provider scans only its history-scope directory, accepts the exact active
filename and numerically valid `.s########.jsonl` siblings, sorts by segment
number, and caps source enumeration at 100,000 files. Cross-conversation search
enumerates `.jsonl` sources under the selected scope/root with the same cap and
reports truncation through existing search statistics.

No durable archival manifest or disposable aggregate catalog was introduced.
The measured 256-source workload remained usable, ordering is recoverable from
the segment names plus authoritative record timestamps, and retention can
reconstruct all metadata directly from source files. A manifest would add
another recovery and atomic-update path without solving a demonstrated
correctness or meaningful latency problem. If future measurements exceed the
100,000-source bound, that is a focused follow-up decision rather than a
reason to make the current manifest-less layout more complex now.

## Retention strategy

`CleanupAsync` remains streaming and now operates on all discovered JSONL
segments. Each source is classified before mutation:

- sources over 256 MiB are skipped rather than deleted;
- malformed/oversized/incomplete records cause the source to be skipped safely;
- a source with no records is left active, while an empty archived sibling can
  be removed;
- a source with no records older than the cutoff is left byte-for-byte alone;
- a wholly obsolete archived segment is deleted directly, including its
  `.hidx` and `.hsidx`; and
- an intersecting source is streamed through a unique temporary JSONL file,
  flushed, atomically replaced, and both sidecars are invalidated.

An active source that becomes wholly obsolete is rewritten to an empty active
file rather than removing the active path. A failed or cancelled temporary
rewrite leaves the original authoritative source in place; temporary files are
removed in `finally` where possible. Cleanup exposes bounded
`ConversationLogCleanupStatistics` for diagnostics and benchmarks: segments
examined/deleted/rewritten/skipped, valid records examined/removed, estimated
bytes read/written, and source enumeration truncation.

## Search across segments

`SearchDetailedAsync` fans out through the existing source-oriented loop. Every
physical segment gets its own sidecar lookup/build and its own authoritative
JSONL read. `.hsidx` Bloom blocks can prune selective text/date searches in
large segments; small segments below the Phase 1I index threshold remain
linear, which is expected for the 16/64/256-small-segment experiments.

The collector still retains only the bounded newest candidate window needed for
`Skip` and `MaximumResults`; it does not accumulate all broad matches. Search
statistics report examined/skipped files, examined records, matches, records
skipped by index, index files used/built, and cold index-build milliseconds.
Selective result locations point to the physical source offset but callers
continue to use logical network/conversation identity.

## Benchmark methodology

The opt-in Release benchmark is
`HistoryPerformanceTests.JsonlSegmentedHistoryScaleReportIsDeterministicWhenExplicitlyEnabled`.
It runs only with:

```powershell
$env:NEXIRC_RUN_HISTORY_PERFORMANCE = '1'
dotnet test tests\nexIRC.Application.Tests\nexIRC.Application.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~JsonlSegmentedHistoryScaleReportIsDeterministicWhenExplicitlyEnabled" --logger "console;verbosity=normal"
```

It writes deterministic generated records and disposes each temporary
workload. It measures cold/warm newest pages, timestamp navigation, bounded
export, selective cold/warm indexed search, broad/common search, and a
date-outside-range discovery/pruning search. Each measurement records wall
milliseconds, returned count, records examined, files examined, matches,
index-skipped records, index files used/built, and cold build time where the
API exposes it. Wall times are engineering measurements on the development
host and are sensitive to filesystem and OS cache state; they are not a
statistically controlled benchmark.

The generated record shape is the same compact deterministic JSONL shape used
by the Phase 1I harness. The million-record one-source variant is intentionally
not included: the generated source is about 432 MB and the existing 256 MiB
reader boundary must reject it. The million-record minimum is therefore two
sources, each below that hard boundary.

### Segmented performance raw measurements

The compact measurement syntax is
`milliseconds / returned / examined / files / matches / index-skipped`.
The raw output additionally includes `index`, `built`, and `build` fields.
The source byte count is the total authoritative JSONL bytes before any
retention mutation in the one retention case.

| Records | Sources | JSONL bytes | Cold newest | Warm newest | Timestamp | Export | Selective cold / warm | Broad common | Discovery/prune |
|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|
| 10,000 | 1 | 4,298,827 | 284 ms | 7 ms | 2 ms | 10 ms | 263 ms / 87 ms | 198 ms | 1 ms |
| 10,000 | 4 | 4,298,629 | 271 ms | 5 ms | 1 ms | 3 ms | 272 ms / 211 ms | 240 ms | 1 ms |
| 10,000 | 16 | 4,297,841 | 179 ms | 184 ms | 86 ms | 6 ms | 198 ms / 201 ms | 217 ms | 210 ms |
| 10,000 | 64 | 4,294,688 | 176 ms | 151 ms | 59 ms | 3 ms | 53 ms / 52 ms | 50 ms | 50 ms |
| 10,000 | 256 | 4,282,078 | 139 ms | 131 ms | 93 ms | 5 ms | 79 ms / 79 ms | 77 ms | 75 ms |
| 100,000 | 1 | 43,088,825 | 433 ms | 9 ms | 3 ms | 6 ms | 500 ms / 26 ms | 359 ms | 0 ms |
| 100,000 | 4 | 43,088,618 | 399 ms | 13 ms | 2 ms | 1 ms | 401 ms / 53 ms | 348 ms | 1 ms |
| 100,000 | 16 | 43,087,794 | 903 ms | 14 ms | 4 ms | 1 ms | 400 ms / 228 ms | 380 ms | 4 ms |
| 500,000 | 1 | 215,888,823 | 2,317 ms | 22 ms | 12 ms | 8 ms | 1,885 ms / 20 ms | 1,744 ms | 0 ms |
| 500,000 | 4 | 215,888,608 | 2,110 ms | 20 ms | 8 ms | 2 ms | 1,884 ms / 55 ms | 1,886 ms | 313 ms |
| 1,000,000 | 2 | 431,888,751 | 4,377 ms | 318 ms | 487 ms | 330 ms | 14,316 ms / 27 ms | 3,478 ms | 0 ms |
| 1,000,000 | 4 | 431,888,607 | 6,275 ms | 768 ms | 31 ms | 38 ms | 11,642 ms / 90 ms | 6,079 ms | 11 ms |

The 500,000/4 and million-record cold values show ordinary host filesystem
cache variance. The 1,000,000/2 and /4 cold indexed-search builds were 14.3 s
and 11.6 s in this run, while earlier runs measured 3.8 s and 5.7 s for the
same deterministic workloads. The warm indexed searches remained 27 ms and
90 ms in the final run. These are engineering observations, not precise
capacity guarantees.

### Segment-count and search metrics

For 10,000 records, the 256-source case examined 256 files and 10,000 records
for a selective marker, returned 10 bounded results, and completed discovery/
date pruning in 80 ms. For 100,000 records, the 1-source warm selective query
examined 4,096 of 100,000 records; the 4-source query examined 16,384; and the
16-source query examined 65,536 because each small segment had one coarse
block. For 500,000 records, warm selective search examined 4,096 records in
one source and 16,384 across four. For one million records, warm selective
search examined 8,192 records across two sources and 16,384 across four.

The first selective query paid cold index construction: 247–270 ms at 10k,
390–499 ms at 100k, about 1.9 seconds at 500k, and 11.6–14.0 seconds at one
million in the final run. Earlier runs measured 3.8–5.7 seconds for the same
million-record cases, making cache variance obvious. Warm selective searches
were 20–27 ms at 500k/1M with one or two large sources and 55–90 ms across
four. Broad/common searches examined every record and remained about 198–240
ms at 10k, 348–380 ms at 100k, 1.7–1.9 seconds at 500k, and 3.5–6.1 seconds
at one million.

### Retention raw measurement

The 100,000-record/16-source workload used a cutoff at record 25,123 so the
boundary intersected one source:

```text
history-segmented-retention records=100000 segments=16 milliseconds=389
removed=25123 examined=100000 deleted=4 rewritten=1
bytesRead=45781475 bytesWritten=2640691
```

Four wholly old archived segments were deleted without rewrite, and one
intersecting source was rewritten. The active/newer sources were preserved.
The focused retention test also verified no temporary files remained and that
sidecars belonging to remaining sources were not orphaned.

### Rotation and many-conversation raw measurements

Actual writer rotation used a 16 KiB threshold and appended 10,000 records:

```text
history-segmented-rotation records=10000 segments=257 milliseconds=15395
```

The many-conversation workload used two networks, 64 logical conversations,
duplicate `#room` names, four sources per conversation, 256 total JSONL
sources, and 27,164,960 bytes:

```text
all=256files/64000examined/10results
currentNetwork=128files/32000examined
duplicateNetwork=4files/1000examined
```

This is the strongest discovery datapoint: filesystem fan-out remains bounded
and visible at 256 sources, while network-qualified and conversation-qualified
queries reduce the source set as expected.

## Failure and recovery behavior

Rotation is serialized by the existing single writer. Sidecars are deleted or
invalidated before an active source is moved, so a moved segment cannot retain
a sidecar whose source path/fingerprint refers to the active name. A failed
rotation returns a failed append and does not fabricate a record.

Sidecar damage remains non-authoritative. The search path validates cached
`.hsidx` content as well as source and sidecar fingerprints, addressing the
same-size/same-timestamp cache edge case. Corrupt or missing `.hidx` and
`.hsidx` files cause a bounded rebuild where supported or a streaming JSONL
fallback. Deleting all sidecars leaves the JSONL sources readable/searchable.

Retention never replaces the original until the complete temporary rewrite is
flushed. Cancellation during classification or rewrite propagates and removes
the temporary path where possible. Invalid source content is skipped rather
than silently treated as an empty segment eligible for deletion.

## Lifecycle, privacy, and draft invariants

Search, paging, export preparation, retention inspection, and archived-result
routing remain observational. They do not JOIN/PART, connect/disconnect, send
IRC traffic, mark read, clear highlights/important state, generate activity or
notifications, reorder recents, duplicate conversations, mutate favorites, or
destroy drafts. Opening an archived result reuses the existing
network-qualified historical-view path and does not JOIN a channel or connect
a disconnected network.

Drafts remain in memory, capped at 4,096 characters, and absent from JSONL,
`.hidx`, `.hsidx`, archive segments, exports, and search results. The new
segmented tests preserved lifecycle state, activity flags, outbound transport
count, and network conversation count while reading/searching/retaining
segmented data. Existing Phase 1I tests continue to cover duplicate names on
different networks and historical/closed/reopened conversations.

## Demo coverage

`--demo` still uses deterministic fake transports and no public network. Demo
mode now creates a temporary `JsonlConversationLogStore` with a 32 KiB segment
threshold and deletes that temporary root on normal application shutdown. It
therefore exercises actual physical rotation rather than the in-memory store.
The seeded scenario includes:

- a several-segment `#alpha` conversation;
- newest/page history and a marker located in an old segment;
- duplicate `#lounge` names on AlphaNet and BetaNet;
- private/query history;
- parted, closed, historical, and disconnected views;
- sender/date-filtered searches;
- reopening an old search result without JOIN;
- favorites and recents remaining present; and
- drafts and important/unread activity remaining scoped and unaffected.

No storage-administration UI was added.

## Automated validation

The final validation was performed in Release after the implementation and
tests were built:

- New Phase 1J tests: 4 passed, 0 failed.
- Application suite: 63 passed, 0 failed when run alone.
- Core suite: 40 passed, 0 failed.
- Networking suite: 23 passed, 0 failed.
- Isolated-suite aggregate: 126 passed, 0 failed across the three suites.
- Opt-in segmented benchmark: 1 passed in about 100 seconds; no generated
  dataset was retained in the repository.
- Release solution build: 0 warnings, 0 errors.
- `dotnet format nexIRC5.sln --verify-no-changes --no-restore`: passed.

One parallel solution test invocation had a flaky application failure in
`Phase1ETests.RichCommandsUseAdaptivePrefixAndKeepQueryActionsNetworkScoped`
(expected `PRIVMSG Other :\x01VERSION\x01` absent from the fake transport
lines). The application suite passed on its isolated rerun; this phase did not
weaken or alter that test.

There is no desktop/UI test project in the repository. No UI test count is
claimed. The WPF smoke launched
`src\nexIRC.Desktop\bin\Release\net10.0-windows\nexIRC.Desktop.exe --demo`,
observed the `nexIRC 5` window as responsive, requested normal close, observed
exit code 0, and confirmed no process remained. The available interactive
Computer Use runtime was not exposed in this session, so no UI clicks,
keyboard actions, search-result clicks, or visual interaction beyond process
and window-state inspection are claimed.

The conservative live smoke connected to `irc.libera.chat:6697` with TLS
authentication, sent CAP LS 302/NICK/USER using a temporary nickname, and
waited 20 seconds. No CAP, numeric 001 registration, or numeric 318 WHOIS
reply arrived, so WHOIS and QUIT were not sent and the result is reported as
**inconclusive/failed**, not as a successful IRC smoke. No channel was joined
and no chat message was sent.

## Limitations

- Cold `.hidx`/`.hsidx` construction remains linear in the source.
- Common-term and broad-date searches remain linear over candidate blocks.
- Small segments below the sidecar thresholds scan linearly, so excessive
  micro-segmentation adds measurable per-file overhead.
- A query is bounded to 100,000 discovered source files; higher scales need a
  new measured decision.
- Source timestamps are the logical ordering key; malformed records are
  skipped under the established safe-read policy.
- The current physical name is a compatibility lookup convention, not a
  general metadata manifest; future migrations may need a versioned layout
  rule if the key derivation changes.
- Wall-clock measurements vary with host filesystem cache and concurrent load.

## Architectural decision and Phase 1K recommendation

**Outcome A. Continue with JSONL + `.hidx` + `.hsidx` + segmented files.**

Do not add a durable archival manifest or aggregate index in Phase 1J. The
current filesystem model is recoverable from authoritative JSONL, retains
logical-conversation semantics, and stayed interactively reasonable through
256 small sources and one million records split into safe physical sources.
Keep retention streaming and prefer whole-segment deletion.

Phase 1K should be a focused comparison only if production telemetry or a
larger deterministic workload crosses the measured wall: cold multi-million
record search construction, sustained broad searches over many networks, more
than 100,000 sources, or unacceptable micro-segment discovery latency. That
phase should compare a compact token/catalog design against the current
segmented source abstraction and measure migration/recovery cost. It should
not assume SQLite/Lucene is required before that workload is demonstrated.
