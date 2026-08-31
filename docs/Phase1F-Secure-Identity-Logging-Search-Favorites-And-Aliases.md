# nexIRC 5 Phase 1F — Secure Identity, Logging, Search, Favorites and Aliases

Status: implemented in the local Phase 1F commit.

Phase 1F adds durable daily-use infrastructure while preserving the Phase 1A–1E
network/session, typed-message, query-lifetime, adaptive-protocol and WPF
foundations. The application configuration remains bounded JSON; secrets and
conversation history have separate storage boundaries.

## Secure credential design

`IProfileCredentialStore` and `ProfileCredentialService` are application-level
interfaces/facades. WPF does not know the protected storage format, and Core
protocol parsing does not know how credentials are stored. Credential identity
is `(profile ID, credential kind)`, never a display name, hostname or nickname.
Renaming a profile therefore does not orphan its credentials.

On Windows the production composition uses `WindowsCredentialStore`, a thin
P/Invoke wrapper over the user-scoped Windows Credential Manager generic
credential APIs in `advapi32.dll`. Targets use the form
`nexIRC5/profile/{stable-profile-guid}/{kind}` and the credential is persisted by
the OS vault, not by nexIRC JSON. The implementation bounds usernames and
protected blobs, frees native structures, and clears temporary byte/character
buffers where possible. Tests and deterministic demo mode use an explicitly
in-memory provider and never touch the real vault.

Supported kinds are SASL, server `PASS`, and an explicit NickServ kind for a
future explicit NickServ workflow. Phase 1F wires SASL and opt-in server PASS
through the current session provider contracts. It does not invent a hidden
NickServ command/password control.

The profile editor has masked SASL and server-PASS fields, stored/not-stored
status, clear controls, optional SASL username, and an opt-in server-PASS
checkbox. A blank masked field preserves an existing secret. Secure-store
failure never falls back to JSON or another plaintext file: the entered value
can be held by a disposable in-memory provider for the current runtime session,
with a diagnostic indicating that it was not persisted.

Missing, unavailable, access-denied, corrupt, failed and deletion-race states
are represented as status results. A failed provider cannot crash receive or
registration processing. Profile deletion calls `ClearProfileAsync`; a stale
vault entry is harmless even if cleanup itself is unavailable.

## Secret exclusion and redaction audit

`NetworkProfile`, `ApplicationPreferences`, `ProfileExportDocument`, and the
normal configuration JSON have no secret-valued properties. The one-way
`ServerPasswordEnabled` flag is omitted when false and contains no secret.
Export has no vault target identifiers. Malicious `password`/SASL-looking JSON
properties are ignored because they are not part of the import model.

`IrcSensitiveData` is the shared redaction helper for PASS and AUTHENTICATE
lines, including leading whitespace and tab-separated forms. Outbound protocol
events/transcripts, raw-command local echoes, and diagnostic-style text use the
redacted representation. Conversation logging redacts a sensitive command
before queueing it. No credentials are written to conversation logs, normal
configuration, profile exports, or ordinary user-facing status paths. Tests
cover PASS, AUTHENTICATE, fake credential providers, export omission and
conversation-log omission.

## Profile portability

The versioned `ProfileExportDocument` currently uses schema version 1 and can
contain a selected subset or all network profiles plus safe global preferences.
It excludes passwords, SASL secrets, vault target names, and transient session
state. Export and import are bounded to 1 MiB, JSON depth 16, the profile limit,
and the existing per-field limits.

Import validates/normalizes hosts, nicknames, channels, SASL policy and all
collections. The default collision policy is `CreateNew`: an ID collision gets
a deterministic SHA-256-derived GUID, while explicit `Skip` and `Overwrite`
policies are available to the application layer. Existing profiles are never
overwritten by default. Unsupported schemas, malformed JSON and oversized
files return diagnostics without changing the configuration. Imported
preferences cannot select an arbitrary active network; the selected profile is
cleared before applying them.

## Conversation logging

Conversation history is structured `ConversationLogRecord` data, not WPF
display objects. Each record includes timestamp, runtime network ID, stable
profile scope where available, conversation type/name/key, sender, semantic
message kind, incoming/outgoing direction, text and highlight state. Typed
distinctions include normal message, action, notice, CTCP, join/part/quit/kick,
nick/topic/mode, system and error records.

Production uses `JsonlConversationLogStore`: one append-friendly UTF-8 JSONL
file per stable scope and SHA-256-derived conversation-key filename. Display
names never become path segments, so invalid path characters and later renames
cannot split or corrupt a log. Different profile/network scopes keep identical
`#general` names and identical nicknames separate. Demo and tests use the
in-memory store.

Writes go through an asynchronous bounded channel of 2,048 records and a
single writer. IRC receive/semantic routing never awaits log I/O. A write
failure is captured as a diagnostic and dropped safely. Files larger than 16 MiB
are ignored by readers, individual serialized records are bounded to 32 KiB,
and cleanup rewrites files atomically after a flush barrier. Startup maintenance
applies retention using the configured age.

Preferences are conservative by default: conversation logging is disabled,
private-message logging and status logging are independently switchable, and
the retention default is 30 days (validated to 1–3,650 days). Disabling a
category prevents it from entering the history store.

## Historical viewer and search

The WPF history surface provides a network selector, conversation selector,
bounded text search and a paged history load. Results show local timestamps,
conversation identity, sender and preview text; message/action/notice/CTCP
semantics remain available on the structured record. History pages are capped
at 100 records and search results at 500.

Search is a case-insensitive bounded scanner over JSONL files. It supports text,
network/profile, conversation kind/name and optional UTC date bounds. It scans
at most 4,096 files, caps queries at 256 characters, skips malformed records,
and retains only a bounded newest candidate window so file enumeration order
cannot hide newer matches. This is intentionally a practical client-log
scanner, not a large search-engine dependency.

Selecting a result routes by runtime network ID when the session is present and
falls back to stable profile ID when a session was recreated. The corresponding
channel, query or status view is opened/focused, so equal names on two networks
cannot cross-route. The viewer also supports loading a bounded older page for a
saved profile/conversation.

## Favorites and recent destinations

Favorites are persisted saved destinations, distinct from auto-join. They are
scoped by stable profile ID (or a temporary runtime network ID for unsaved
connections), carry channel/query kind and an optional friendly label, and are
deduplicated with RFC1459 IRC case folding. The limit is 128 favorites per
profile scope.

Recent destinations are persisted newest-first MRU entries. They are deduped by
scope, kind and RFC1459-folded name, with separate limits of 50 recent channels
and 50 recent queries per scope. Reopening updates recency; clear-history is
available for the active scope. Transient WHOIS/LIST views are not retained as
destination noise.

The main Tools menu opens a focused Favorites and recents window with add
current, open, remove favorite and clear-recents actions. Opening a channel
favorite creates/focuses the correct network-local view and joins it if needed;
opening a query favorite creates/focuses the correct network-local query.

## Aliases and custom commands

Aliases are persisted definitions with name, expansion, enabled state and
optional description. Names are normalized case-insensitively and limited to
letters, digits, `_` and `-`; expansion length is capped at 1,024 characters.
There can be at most 128 aliases. Built-in commands remain authoritative:
attempts to create an alias named like `/join`, `/whois`, or another supported
built-in are rejected deterministically.

Argument substitution supports `$1`, `$2`, and so on, replacing a missing
positional argument with an empty string. `$*` means all parsed arguments joined
by single spaces. The expander accepts simple single/double quote grouping for
argument parsing; it is not a shell and does not execute a process, access a
filesystem, or evaluate arbitrary code.

Expansion is recursive only through the ordinary nexIRC command dispatcher,
with a maximum depth of 8 and deterministic loop detection. An alias may call
another alias, a built-in command, or an established raw IRC command path, but
all execution stays attached to the selected network/session. The command
editor is a separate modest WPF window for list, add, edit, enable/disable and
delete operations with validation feedback. `/help` lists built-ins and enabled
user aliases.

The Phase 1D completion engine now searches enabled aliases as well as built-ins.
Built-ins are listed first and cannot be shadowed, so completion and dispatch
share the same precedence policy.

## Notifications and activation routing

Notifications now carry a typed `NotificationActivationTarget` containing
network ID, optional stable profile ID, view ID, view kind and conversation
name, plus their semantic notification type. The application can route an
activation to an existing view or recreate the correct profile-scoped channel,
query or status view. The taskbar-balloon adapter invokes that route on click
when the host exposes the click callback.

The adapter remains an unpackaged WPF/taskbar-balloon fallback. Phase 1F does
not claim native Windows toast activation. Notification delivery remains
asynchronous and independent of IRC receive processing. A three-second
coalescer suppresses repeated inactive highlights or PM notifications for the
same network-local view/type, with a 256-entry bound; active views, own messages
and non-highlight/non-PM notification types remain governed by the existing
preference policy.

## View-state persistence

Validated preferences persist selected profile, window size/position/maximized
state, navigation/member pane widths and log filters where available. Widths,
heights, pane sizes and text are bounded; non-finite dimensions fall back to
sane defaults. Restored coordinates are checked against the current work area
with a visible overlap margin, so a previous multi-monitor layout cannot leave
the window permanently off-screen. Protocol state, secrets and live views are
not persisted.

## Bounds

The primary bounds are centralized in `ConfigurationLimits`: 64 profiles, 128
aliases, alias names 32 characters, alias expansions 1,024 characters, alias
depth 8, 128 favorites per scope, 50 recent channels and 50 recent queries per
scope, 32 KiB log records, 256-character searches, 500 search results, 100
history records per page, 256 notification-coalescing entries, and 1 MiB
configuration/export files. The JSONL store additionally caps a readable file
at 16 MiB and scans 4,096 files. Invalid persisted data is normalized or
discarded before it reaches WPF/session objects.

## Demo, tests and validation

Deterministic `--demo` mode uses fake transports, in-memory configuration,
in-memory credentials and in-memory logs. It creates AlphaNet and BetaNet with
overlapping channel/nickname names, seeds history and a fake stored/not-stored
credential state, creates favorites/recents and aliases, exercises alias
completion and local alias execution, and provides data for history/search and
network-specific activation routing. It does not write real user state or use
the Windows vault.

Automated coverage includes credential lifecycle/failure/scoping, server-PASS
provider bridging, no-secret configuration/export/log paths, import validation
and deterministic collisions, JSONL append/search/page/retention/isolation,
favorites/MRU bounds and RFC1459 dedupe, alias substitution/loops/depth/
completion/precedence, notification coalescing and activation data, view-state
repair, and the existing Core/Networking regression suite. The final suite is
103 passing tests: 40 Core, 23 Networking and 40 Application (there is no
separate Desktop test project).

The Release solution build completed with zero errors and zero warnings, full
Release tests passed, and `dotnet format nexIRC5.sln --verify-no-changes
--no-restore` passed. The real Release WPF process launched in `--demo` mode and
closed cleanly with no orphan process. The available Windows capture helper
reported the WPF HWND under a stale unrelated executable identity, so
accessibility/screenshot-driven clicks through profile, preferences, favorites,
alias and log windows were not claimed.

A bounded anonymous live smoke reached Libera.Chat over TLS, completed CAP and
numeric 001 registration, sent WHOIS for the temporary nick `nexF1Smoke`,
received numeric 318, and cleanly disconnected with QUIT. It did not use stored
credentials, join channels or message other users.

## Remaining limitations and recommended Phase 1G

- Native Windows toast activation is still deferred; taskbar-balloon click
  routing is abstracted and tested, but native toast behavior is not claimed.
- Search is a bounded JSONL scanner; a future indexed store can improve global
  search for very large histories without changing the record model.
- History UI currently exposes one bounded page at a time; richer date pickers,
  infinite-scroll loading and matched-term highlighting are natural follow-ups.
- NickServ credentials have a secure storage kind but no explicit Phase 1F
  NickServ workflow, by design; arbitrary command text never becomes a secret
  storage channel.
- The WPF HWND/accessibility helper needs repair before full scripted manual UI
  validation can be claimed.

Recommended Phase 1G: build on these stable identities and records with indexed
history navigation/export, richer favorite grouping, safe contextual alias
variables, native toast evaluation, and more mature channel/query lifecycle
tools while preserving the bounded, network-scoped contracts.
