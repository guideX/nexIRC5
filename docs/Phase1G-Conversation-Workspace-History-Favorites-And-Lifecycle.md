# nexIRC 5 Phase 1G — Conversation Workspace, History, Favorites and Lifecycle

Status: implemented locally on top of the Phase 1F baseline.

Phase 1G makes conversations durable workspace objects rather than disposable
tabs. It extends the Phase 1F application services and WPF shell while keeping
protocol parsing in Core, persistence in application services, and UI behavior
in Desktop.

## Architecture and identity

`NetworkWorkspace` owns the network-local logical collections of channels and
queries. `Views` contains only views currently shown in the workspace tree;
`Channels` and `Queries` retain logical conversation objects after a view is
closed. `EnsureChannel` and `EnsureQuery` use the active network's advertised
IRC casemapping, so reopening an existing conversation returns the same object
and cannot merge equal-looking names from another network.

Stable profile IDs remain the persistence scope when a saved profile exists.
Unsaved connections use their runtime network ID. History routing first tries
the runtime network ID and then the stable profile ID, while the conversation
kind/name remains part of the target. This prevents a `#lounge` or `Mira`
record from crossing networks.

## Conversation lifecycle

Closing a channel or query view removes it from `Views` and marks it closed; it
does not PART, remove the logical object, delete its entries, or delete JSONL
history. Reopening through a favorite, recent destination, search result,
`EnsureChannel`/`EnsureQuery`, or the explicit reopen path inserts the existing
object back into the correct location and focuses it.

Channels expose joined, parted, disconnected, and historical states. A channel
becomes `parted` after explicit PART intent when the connection is usable, and
`disconnected` when a network session is lost or being replaced. Reconnect
resets member projection while preserving the channel object and transcript;
the server's next JOIN/synchronization updates it back to joined. An explicit
REJOIN command is available and has a small recovery path for the interval
where a server has not echoed the preceding PART.

Queries expose active, disconnected, and historical states. Closing a query is
presentation-only, and reopening by RFC1459-equivalent nickname returns the
same query object. Historical-only channel/query activation is available to
the application layer for demo/history navigation.

These operations remain distinct:

* Close View changes only UI visibility.
* Part Channel removes channel desired-join intent and sends PART when
  registered.
* Disconnect stops the network session but preserves workspace objects.
* Rejoin explicitly restores channel desired-join intent and sends JOIN as
  needed.

The tree uses text markers (`●`, `○`, `◌`, `◇`) and lifecycle tooltips in
addition to unread/highlight markers. The active view, joined/parted state and
unread/highlight state are therefore not communicated by color alone.

## Clear behavior

`/clear`, the IRC menu item, toolbar action, and conversation context action
clear the active view's displayed entries and any temporary history context.
They do not touch the authoritative JSONL store. Persistent-history deletion is
intentionally not included in this phase.

## History navigation and search context

The authoritative store remains bounded JSONL. `HistoryPageRequest` and
`HistoryPage` provide newest, oldest, older, newer, and around-timestamp
windows. Pages are capped at 100 records. JSONL navigation scans one scoped
file at a time and retains only a bounded candidate page; it does not load a
whole large file into an unbounded collection. Files over the existing 16 MiB
read bound and malformed/oversized records are skipped safely.

Jump to Date validates a local date/time and requests a bounded window around
that timestamp. No-match and page-boundary states are reported without
changing the active conversation. The history window preserves profile scope,
conversation kind/name, local timestamps, sender, and semantic message kind.

Search result activation opens the correct existing or logical conversation,
routes by runtime ID with profile fallback, and loads a bounded surrounding
history context into the view. The matching record is marked separately from
the context records, so opening a result is not merely an isolated one-line
navigation.

## History export

The history window provides explicit export to UTF-8 plain text or structured
JSONL. The request can be bounded by the selected conversation and optional
date window; records are capped at 10,000 and output is capped at 1 MiB.
Exports use a temporary file and atomic replacement, deterministic ordering,
safe user-selected paths, timestamps, direction/semantic fields, sender and
conversation identity. Internal JSONL storage and user exports are distinct.
Records are defensively redacted before plain-text export, and no credential
provider, secret, vault target, or plaintext password is part of the export
model.

The Phase 1F scanner is adequate for this phase: per-conversation files are
already hash-addressed, bounded, and small enough for one-file page scans. No
heavy database or full-text index was added. A disposable offset/index can be
evaluated later if measured history sizes make jump/search latency material;
JSONL remains authoritative either way.

## Favorites and recent destinations

Favorites now have persisted, bounded groups. The stable `General` group is
created for existing Phase 1F favorites that have no group field. Groups are
limited to 32 and favorites to 128 per scope. Favorites retain network/profile
scope, destination kind, RFC1459-aware duplicate suppression, labels and
deterministic group/item ordering. The Favorites window supports adding a
group, moving a favorite between groups, moving it up/down, opening it,
removing it, and adding the current destination. Removing a group moves its
favorites to `General`.

Recent channels and queries remain separate 50-entry MRU collections per
scope. Reopening deduplicates and refreshes recency. The window supports
opening, removing a selected recent destination, clearing channel recents, and
clearing query recents. Equal names on different profile/network scopes remain
separate.

## Contextual aliases

Aliases remain bounded convenience macros, not a scripting runtime. In addition
to `$1`, `$2`, `$*`, aliases support these safe contextual variables:

* `$network` — server-advertised network name, falling back to display name.
* `$profile` — saved/profile display name.
* `$target` — active channel or query target.
* `$me` — current session nickname.
* `$server` — configured server hostname.
* `$selected` — selected nickname when a caller supplies one; otherwise empty.
* `$$` — one literal dollar sign.

Missing context expands to an empty string. Expansion uses deterministic token
parsing, preserves the depth-8 recursion bound and loop detection, keeps
built-in command precedence, and never evaluates shell/environment/filesystem
expressions or launches a process. Completion and `/help` continue to expose
the same built-in-first policy.

## Context menus and drafts

Channel context actions include join/part/rejoin, close-without-part, clear,
favorite toggle, copy name, mode/topic requests, history/search entry and
LIST. Query actions include close, clear, favorite toggle, copy nickname and
history/search entry. Network and member actions from Phase 1F remain.

Unsent input is retained in memory per `(network ID, view ID)` while navigating
within the running client, capped at 4,096 characters. It is restored when the
same logical view is reopened during that run. Draft text is not persisted,
sent, or logged as conversation history; application restart clears it.

## Notifications and native toast decision

The existing typed taskbar-balloon adapter remains in use. It preserves
coalescing, active-view/own-message suppression and typed runtime/profile/view
routing. Native Windows toast activation was evaluated but deferred: this
unpackaged WPF application does not currently have a stable installer-owned
AppUserModelID/COM activation registration, and adding one here would create a
fragile packaging dependency. No native-toast claim is made for Phase 1G.

## Demo scenarios

`--demo` uses fake transports, in-memory configuration/credentials/logs and no
real credentials. It shows AlphaNet and BetaNet with duplicate `#lounge` and
`Mira` identities, joined and parted channel states, an active and a
historical-only query, grouped favorites, channel/query recents, contextual
aliases, more than one history page, search context, and typed notification
routing. Demo history records are clearly fake and remain in memory.

## Persistence compatibility and bounds

The configuration schema remains compatible with Phase 1F. New favorite group
and favorite order fields are additive; missing groups normalize into the
stable `General` group. All new collections and names are bounded and malformed
values are discarded or normalized before WPF observes them. Credentials
remain exclusively behind the Phase 1F credential abstraction and are absent
from configuration, history, export, and demo data.

## Test and validation scope

Application tests cover contextual expansion/missing context/escaping,
favorites groups/order/moves/removal/collision isolation, bounded history
page edges and around-date selection, malformed JSONL and export formats, and
close/reopen/part/rejoin/disconnected lifecycle behavior. Existing Core,
Networking, and Application coverage remains in place. There is no separate
Desktop UI test project; Release `--demo` startup/shutdown is used for shell
validation, and the known accessibility helper identity limitation is not
presented as click automation.

The final local validation ran 40 Core tests, 23 Networking tests, and 49
Application tests (112 total) in Release. The solution Release build completed
with zero warnings and zero errors, and `dotnet format nexIRC5.sln
--verify-no-changes --no-restore` passed. Release `--demo` started, stayed
alive, closed through the WPF window-close path with exit code 0, and left no
matching nexIRC process. A supplemental live Libera TLS run with a temporary
nickname completed CAP/registration/self-WHOIS/318 and clean QUIT without
joining a channel or sending a chat message.

## Deferred work

Native toast/AppUserModelID packaging, persistent drafts, destructive history
deletion, a disposable history offset index, drag/drop favorite ordering, and
full UI automation remain deliberately deferred. DCC, scripting runtimes,
plugins, file transfer, bouncers, and other out-of-scope protocol/platform
features are not part of Phase 1G.
