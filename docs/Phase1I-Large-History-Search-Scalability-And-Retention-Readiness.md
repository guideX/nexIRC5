# nexIRC 5 Phase 1I — Large-History Search Scalability, Cross-File Search, and Retention Readiness

## Result

Phase 1I reached **Outcome B — lightweight search acceleration justified**.

The Phase 1H JSONL design is still appropriate as the authoritative store and
for newest-page/date navigation. A first search over a previously unindexed
large file is necessarily a full streaming pass, but repeated selective
searches became materially faster once a small disposable sidecar was built.
Common-term and broad-range searches still scan matching blocks linearly; this
is intentional and avoids turning the sidecar into a second message database.

## Starting state

- Repository: `D:\dev\nexIRC\nexIRC5`
- Branch: `main`
- Starting HEAD: `5c7e27988a3b2b7f47a1274896b814d83083aa5e`
- Starting subject: `Implement nexIRC 5 Phase 1H conversation navigation and history performance`
- Starting worktree: clean
- Upstream: `origin` → `git@github.com:guideX/nexIRC5.git`, branch `origin/main`
- Starting ahead/behind: `0/0`
- No fetch, pull, merge, rebase, or push was performed.

The verified local graph at preflight was:

```text
origin/main -> 5c7e279 Phase 1H -> ecf34a8 Phase 1G -> earlier phase commits
```

## Phase 1H audit

`JsonlConversationLogStore` writes one authoritative JSONL file per scoped
conversation. The path is the history scope ID plus a shortened SHA-256 of the
conversation key, so the same RFC1459-equivalent target name on two networks
cannot collide. Search and navigation validate records while reading and skip
malformed JSON, overlarge records, and an incomplete final line according to
the existing safe-read policy.

The existing `.hidx` is deliberately not a content index. It contains bounded
timestamp/offset/length entries for page, date, and export navigation. It is
versioned, fingerprinted with source length and last-write ticks, written to a
temporary file, atomically replaced, and safely rebuilt when missing, stale,
truncated, or corrupt. JSONL remains authoritative.

The existing limits audited in this phase are:

- 100 records per history page;
- 10,000 records per bounded export/range;
- 1 MiB export output;
- 4,096 searchable JSONL files per search;
- 500 returned search results, with bounded skip/paging;
- 256-character search text;
- 256 MiB maximum readable/writable JSONL file;
- 4,096-character in-memory drafts, never persisted or indexed.

The 256 MiB history limit is per JSONL conversation file. Before Phase 1I,
the writer did not enforce the bound even though readers refused oversized
files. Phase 1I makes the behavior explicit: if an appended serialized record
would cross the per-file bound, the write is dropped with a diagnostic; there
is no silent truncation or implicit rotation. Files above the limit remain
unreadable by the bounded reader and are not indexed. Segmented archival is
left for a later retention phase.

The audit also found that cleanup previously assembled all retained records in
memory. It now streams valid retained records through a temporary JSONL file
and atomically replaces the source, preserving the bounded retention behavior
without making cleanup proportional to retained-message memory.

## Benchmark methodology

The opt-in test is
`HistoryPerformanceTests.JsonlHistoryPerformanceReportIsDeterministicWhenExplicitlyEnabled`.
It runs only when `NEXIRC_RUN_HISTORY_PERFORMANCE=1`; ordinary test runs do not
generate large files. The final run used Release binaries, deterministic JSONL
records, a 100-record page, a 500-record bounded export, and the following
workloads:

- cold and warm newest-page reads;
- oldest, previous, next, and around-date navigation;
- bounded export;
- early, middle, late, no-match, common-term, narrow-date, and broad-date
  searches;
- current-conversation, current-network, workspace/global, and cross-file
  searches where applicable.

The 1,000 through 500,000 record datasets used one conversation file. The
1,000,000 record dataset used two conversation files to exercise cross-file
ordering and scope selection. Timings are wall-clock engineering measurements
on the development host, not statistically rigorous microbenchmarks.

### Final benchmark results

The compact output form is `milliseconds / returned / examined / files`.
Search rows also report index behavior in the notes below.

| Dataset | Layout | JSONL bytes | Cold newest | Warm newest | Around/date seek | Bounded export |
|---:|:---:|---:|---:|---:|---:|---:|
| 1,000 | single | 432,829 | 46 ms / 100 | 14 ms / 100 | 22 ms / 100 | 12 ms / 500 |
| 10,000 | single | 4,338,827 | 217 ms / 100 | 3 ms / 100 | 1 ms / 100 | 3 ms / 500 |
| 100,000 | single | 43,488,825 | 543 ms / 100 | 6 ms / 100 | 4 ms / 100 | 5 ms / 500 |
| 250,000 | single | 108,888,823 | 958 ms / 100 | 21 ms / 100 | 9 ms / 100 | 15 ms / 500 |
| 500,000 | single | 217,888,823 | 1,918 ms / 100 | 26 ms / 100 | 14 ms / 100 | 14 ms / 500 |
| 1,000,000 | split, 2 files | 439,888,751 total | 2,947 ms / 100 | 1,036 ms / 100 | 12 ms / 100 | 41 ms / 500 |

The warm navigation numbers include the existing `.hidx` path and show that
newest-page/date navigation is not the measured bottleneck at these sizes.
The 1,000,000-record warm/newest and adjacent-page readings were affected by
host disk-cache/IO variance in this final run; the selective search results
and examined-record counts are the more stable architectural signal.

### Search results

The first search for each large file builds the content sidecar while doing a
single full parse. This is the linear baseline and avoids a second full parse.
Subsequent rows use the sidecar when its block metadata proves that blocks can
be skipped.

| Dataset | First/early match | Middle match | Late match | No match | Common term | Narrow date | Broad date |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | 34 ms, 1,000 examined | 18 ms, 1,000 | 18 ms, 1,000 | 21 ms, 1,000 | 21 ms, 1,000 | 18 ms, 1,000 | 23 ms, 1,000 |
| 10,000 | 223 ms, 10,000 examined, build 221 ms | 82 ms, 4,096 examined | 361 ms, 10,000 | 178 ms, 0 examined | 200 ms, 10,000 | 85 ms, 4,096 | 177 ms, 10,000 |
| 100,000 | 522 ms, 100,000 examined, build 522 ms | 13 ms, 4,096 | 84 ms, 26,272 | 0 ms, 0 examined | 352 ms, 100,000 | 14 ms, 4,096 | 331 ms, 100,000 |
| 250,000 | 932 ms, 250,000 examined, build 931 ms | 14 ms, 4,096 | 98 ms, 28,816 | 0 ms, 0 examined | 824 ms, 250,000 | 13 ms, 4,096 | 824 ms, 250,000 |
| 500,000 | 1,803 ms, 500,000 examined, build 1,802 ms | 17 ms, 4,096 | 113 ms, 28,960 | 0 ms, 0 examined | 1,660 ms, 500,000 | 14 ms, 4,096 | 1,649 ms, 500,000 |
| 1,000,000, split | 4,904 ms, 1,000,000 examined, build 4,903 ms | 28 ms, 8,192 | 542 ms, 139,840 | 0 ms, 0 examined | 3,287 ms, 1,000,000 | 13 ms, 4,096 | 3,299 ms, 1,000,000 |

For the split million-record dataset, current-conversation search was 15 ms
with 4,096 records examined in one file, current-network search was 26 ms
with 8,192 examined across two files, workspace/global selective search was
26 ms with 8,192 examined, and a broad cross-file common-term search was
3,299 ms with all 1,000,000 records examined. The worst measured search case
was the 4.9-second first early-match query while building two indexes; it is
the expected one-pass linear cost. The broad cross-file common-term query is
the worst steady-state search case and is bounded to ten returned results in
the harness.

The results establish the useful boundary: unindexed first use and
common/broad queries remain linear, while selective repeated queries do not
pay the full multi-hundred-thousand-record scan. A full-text database was not
justified by this evidence.

## Lightweight search acceleration

The new disposable sidecar is `NEXSIDX1`, stored as `<conversation>.jsonl.hsidx`.
It contains no message text and no draft data. Its bounded contents are:

- source length and last-write fingerprint;
- up to 4,096 coarse blocks of 4,096 records;
- block byte offset/length and record count;
- minimum/maximum timestamp per block;
- a 128-byte Bloom filter of case-folded text/sender trigrams;
- a trailing SHA-256 checksum over the sidecar payload.

The sidecar is used only for JSONL files from 1 MiB through 256 MiB. A
missing, stale, structurally invalid, checksum-invalid, truncated, or
partially written sidecar is ignored. The first search then builds it during
the authoritative scan; the sidecar is written through a unique temporary
file, flushed, and atomically replaced. If sidecar writing fails, search still
returns the JSONL results and keeps the in-memory scan outcome. Source appends
invalidate the cached index; source fingerprint checks also catch changes
between store instances. Search falls back to safe streaming JSONL whenever
the sidecar is unavailable.

The sidecar has bounded growth (about 0.7 MiB at the maximum block count,
plus fixed metadata and checksum) and bounded in-memory block metadata. It is
safe to delete and never needed to recover history. The probabilistic filter
only skips a block when every queried trigram is present; candidate blocks are
always rechecked against authoritative JSONL, so Bloom false positives cost
time but cannot create false matches. Non-ASCII terms conservatively bypass
the trigram filter and use JSONL matching.

## Cross-file search model

`IConversationLogStore.SearchDetailedAsync` now returns a typed,
navigation-ready `ConversationLogSearchPage` rather than requiring the UI to
interpret raw JSON. The bounded result fields include network ID, history
scope/profile ID, conversation kind/name, timestamp, sender, message kind,
bounded preview, and a `ConversationLogSearchLocation` containing scope,
conversation key, timestamp, and optional source offset/length.

Supported scopes are:

- current conversation;
- current network;
- all known history.

Queries support text, network, history scope/profile, RFC1459-aware
conversation name, conversation kind, RFC1459-aware sender, message/event
kind, inclusive UTC date range, maximum results, and bounded skip. Search
enumerates at most 4,096 files and retains at most the bounded result window;
the collector keeps newest results without loading every match into WPF.
Results are returned newest-first with deterministic tie-breaking. The
abstraction is file-source based, so future segmented/archival providers can
implement the same caller-facing search contract without changing the WPF
search workflow.

## Cancellation, UI, and navigation

The application layer passes cancellation through flush, file enumeration,
indexed range reads, JSONL reads, and result collection. The WPF log viewer
uses a lifetime cancellation source: starting a replacement search cancels
the obsolete operation, and closing the window cancels and disposes its
work. Exceptions are observed and converted to a bounded status message.

The viewer now exposes scope, network, conversation kind, sender, message
kind, optional date range, result status, cancel, previous/next result,
open-result, copy, and bounded export/history controls in the existing
desktop design. Opening a result routes by network-qualified identity and
uses the existing historical-view path. It can reopen a closed logical view,
but it does not JOIN a channel or PART anything. Search and paging remain
observational: they do not mark read, clear important/highlight activity,
change lifecycle, reorder recents, create notifications, alter drafts, or
send IRC commands.

## Lifecycle and privacy invariants

Regression coverage preserves duplicate target names on different networks,
RFC1459 target comparison, joined/parted/disconnected/historical/closed/
reopened states, favorites, recents, and the 64-entry navigation history.
Search result opening does not duplicate logical conversations and does not
implicitly join a parted or historical channel.

Drafts remain in-memory, network/view scoped, capped at 4,096 characters, and
absent from JSONL, `.hidx`, `.hsidx`, exports, and search results. The new
sidecar hashes only information already present in history and stores no
plaintext message or sender text.

## Demo coverage

`--demo` remains deterministic and startup-sized. It now exercises current
conversation search, all-history duplicate `#lounge` targets on two networks,
sender/date filters, joined and parted channels, a disconnected network,
historical and closed views, reopening a search result, favorites, recents,
and important/unread activity remaining unaffected by searches. The benchmark
harness, rather than normal demo startup, owns the 500,000 and 1,000,000
record datasets.

## Validation

Final validation used the exact Release binaries produced from this worktree:

- Core tests: 40 passed.
- Networking tests: 23 passed.
- Application tests: 58 passed, including four Phase 1I correctness/lifecycle
  tests, two history-index tests, and the opt-in performance report when
  explicitly enabled.
- Aggregate: 121 passed, 0 failed.
- UI/Desktop test suite: none exists in the repository; no UI test count is
  claimed.
- Release solution build: 0 warnings, 0 errors.
- `dotnet format nexIRC5.sln --verify-no-changes --no-restore`: passed.
- Opt-in performance: the six-dataset matrix passed in 40.9 seconds and
  emitted the measurements above; the companion index rebuild test also
  passed in the full Application run.

WPF smoke validation launched
`src\nexIRC.Desktop\bin\Release\net10.0-windows\nexIRC.Desktop.exe --demo`.
After five seconds the process reported title `nexIRC 5`, was responsive, and
accepted the normal main-window close message with exit code 0; no orphan
process remained. The available Windows automation helper could not reliably
bind the returned HWND because it reported a stale/mismatched owner after
refresh. Consequently, no interactive clicks, keyboard actions, or visible
search results are claimed.

A conservative live smoke reached `irc.libera.chat:6697` over TLS with a
temporary nickname, sent `CAP LS 302`, completed registration (numeric 001),
completed self-WHOIS (numeric 318), and sent clean QUIT. No channel was joined
and no chat message was sent. No CAP LS reply was observed in this run; this
was recorded as an observation rather than treated as a Phase 1I failure.

## Remaining limitations and Phase 1J recommendation

The architecture still has intentionally linear costs for a cold sidecar
build, broad/common-term searches, files above the 256 MiB per-file readable
bound, and up to 4,096 files per query. The content sidecar is a coarse
candidate filter, not ranking, phrase, regex, or full-text indexing. Reliable
interactive desktop automation depends on the host window-helper state.

Phase 1J should measure years of segmented history and retention operations
before changing storage. If real users hit the per-file ceiling, introduce
explicit append-only archival segments with a manifest and retention policy,
then let the existing source-oriented search abstraction fan out across
segments. Only if segmented measurements show selective search is still too
slow should Phase 1J consider a stronger token or full-text architecture.
