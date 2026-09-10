# Phase 28 — Multi-Server IRCv3 Interoperability

## Closeout status

Phase 28 implementation is complete, but this workstation cannot produce live InspIRCd evidence. The official InspIRCd v4.12.0 Windows package was reachable and was downloaded to a system temporary directory for inspection. Its installer requires administrator elevation; a user-scoped silent install did not produce a runtime, and no pre-existing InspIRCd, Docker, Podman, or WSL runtime was available.

The recorded result is therefore Outcome C:

`SECOND_SERVER_ENVIRONMENT_BLOCKED`

This is an environment limitation, not a claim that InspIRCd behavior was verified. The existing Ergo Phase 27 evidence remains the only live-server evidence in this checkout.

## Scope and evidence boundary

The test tool now distinguishes:

| Concern | Ergo Phase 27 | InspIRCd Phase 28 |
| --- | --- | --- |
| Implementation | Ergo v2.19.1 | v4.12.0 package selected; runtime unavailable here |
| Setup ownership | External testnet service | Disposable localhost launcher |
| Transport | TLS, `testnet.ergo.chat:6697` | Plaintext, loopback, ephemeral port |
| Capability evidence | Live CAP/ISUPPORT and scenario result | Not observed; launcher and profile definitions are ready |
| History evidence | CHATHISTORY passed in the Phase 27 artifact | Not observed |
| Client-only tag policy | Relay passed; `CLIENTTAGDENY` absent | Configurable as `all`, `known`, or `none` when run |

The baseline report is [Phase27-Ircv3-Real-Server-Interoperability.md](Phase27-Ircv3-Real-Server-Interoperability.md), and its machine-readable artifact is [live-result.json](../artifacts/phase27/live-result.json). Phase 28 artifacts are ignored by Git and are written under `artifacts/phase28/` when the launcher runs.

## Profile-driven harness

`src/nexIRC.Interoperability/InteroperabilityProfiles.cs` contains data-only profiles for the Ergo baseline and the local InspIRCd matrix. `Program.cs` consumes the selected profile without duplicating scenario implementations.

The local profiles cover:

- full IRCv3 modules;
- no `echo-message`;
- no `ircv3_msgid`;
- `ircv3_ctctags` with `clientonlytags="known"`;
- `ircv3_ctctags` with `clientonlytags="none"`;
- no `message-tags` module;
- no `server-time`.

Every live scenario reads the negotiated capability state before sending optional commands. Scenario evidence is classified as `Passed`, `Failed`, `Unsupported`, `BlockedByServer`, `NotApplicable`, or `Inconclusive`. In particular, history reports the distinction between an unadvertised `draft/chathistory`, a missing required substrate (`batch`, `server-time`, or `message-tags`), a usable substrate that is not applicable to the run, and a request that was advertised but failed to complete.

The harness uses client B's relayed canonical message ID as the relationship key. This keeps reply and reaction checks valid when `echo-message` is absent and client A has only a local optimistic copy. IDs remain opaque: no server-specific format, case folding, ordering, or numeric assumption is introduced.

## Disposable InspIRCd setup

The template is [inspircd.conf.template](../scripts/phase28/inspircd/inspircd.conf.template). The launcher is [run-phase28-inspircd-interoperability.ps1](../scripts/run-phase28-inspircd-interoperability.ps1); it:

1. resolves an explicitly supplied or standard InspIRCd executable;
2. allocates a loopback port and creates a per-run config/data/log/runtime directory under the ignored Phase 28 artifact area;
3. expands only the requested module/profile configuration;
4. starts the exact executable with `--config`, `--nofork`, and `--nopid`;
5. waits for that process to open the selected loopback port;
6. runs the shared harness and writes sanitized JSON/JSONL evidence;
7. stops only the recorded process ID and reports cleanup.

The ordinary data-driven entry point is [run-phase28-ircv3-interoperability.ps1](../scripts/run-phase28-ircv3-interoperability.ps1). Examples:

```powershell
pwsh -NoProfile -File .\scripts\run-phase28-inspircd-interoperability.ps1 -Profile inspircd-local-full -RunVarianceProfiles
pwsh -NoProfile -File .\scripts\run-phase28-ircv3-interoperability.ps1 -Profile ergo-testnet
```

The launcher does not install software, elevate, alter services, or kill unrelated processes. If no runtime is available it writes `artifacts/phase28/inspircd-result.json` and `artifacts/phase28/server-comparison.json` with `SECOND_SERVER_ENVIRONMENT_BLOCKED` and exits nonzero.

The selected package was the official [InspIRCd-4.12.0.exe](https://github.com/inspircd/inspircd/releases/download/v4.12.0/InspIRCd-4.12.0.exe) Windows asset. Its SHA-256 was `B6027C4835AE9BA160E0D46C5372F71C3505640E0A03F9CD2E60611E0E25EC38`; the downloaded installer was kept only in the system temporary directory and is not a repository artifact. The selected InspIRCd module choices are based on the official v4 documentation: `ircv3_ctctags` provides `message-tags` and controls client-only tag filtering; `ircv3_msgid`, `ircv3_servertime`, `ircv3_echomessage`, and `ircv3_accounttag` provide the corresponding optional behavior; and `chanhistory` is join-time history rather than the IRCv3 `CHATHISTORY` command. See the [Windows installation guide](https://docs.inspircd.org/4/installation/windows/), [IRCv3 client-to-client tags](https://docs.inspircd.org/4/modules/ircv3_ctctags/), [message IDs](https://docs.inspircd.org/4/modules/ircv3_msgid/), [server-time](https://docs.inspircd.org/4/modules/ircv3_servertime/), [echo-message](https://docs.inspircd.org/4/modules/ircv3_echomessage/), [account-tag](https://docs.inspircd.org/4/modules/ircv3_accounttag/), and [channel history](https://docs.inspircd.org/4/modules/chanhistory/) documentation.

## Regression coverage

`tests/nexIRC.Core.Tests/Phase28ProtocolTests.cs` adds deterministic coverage for:

- mixed-case and punctuation-bearing opaque server IDs;
- the `draft/msgid` alias;
- opaque `+reply` references and unsafe-value rejection;
- optional `account` and `time` tags without changing canonical identity.

The existing Phase 27 protocol, networking, and application tests remain in place. No production protocol behavior was changed for this phase; the implementation changes are in the interoperability tool, launcher, profile metadata, and test/docs artifacts.

## Verification record

Starting state:

- branch: `main`;
- starting commit: `4973e757feaf34b413f7345bc85bfac8cdbb34ba` (`Validate nexIRC 5 IRCv3 message interoperability`);
- starting worktree: clean;
- actual starting `origin/main...HEAD`: `0 behind / 0 ahead` (the requested expected `0 behind / 1 ahead` was not true in the checkout).

Verification completed during Phase 28:

- interoperability project Debug build: passed with zero warnings and zero errors;
- focused Phase 28 protocol tests: 4 passed;
- pre-change Core baseline: 110 passed;
- pre-change Networking baseline: 46 passed;
- pre-change Application baseline: the initial broad run observed 179 passed, 1 skipped, and 1 deterministic failure in `Phase1ZIdentityAndContextIntegrationTests.ExtendedJoinAccountEvidenceCanStrengthenAnExistingLiveQuery`; the final broad rerun passed 180 with 1 skipped, so the initial failure was not reproducible after the unchanged production code was rebuilt;
- focused Phase 25/26 coverage: 4 Networking tests and 12 Application tests passed;
- isolated Phase 1R coverage: 1/1 passed;
- final Core full suite: 114/114 passed;
- final Networking full suite: 46/46 passed;
- final Application full suite: 180 passed, 1 skipped;
- final Debug and Release solution builds: passed with zero warnings and zero errors;
- final deterministic WPF reply and reaction smokes: both passed;
- PowerShell parse checks: launcher and generic runner passed;
- local launcher smoke: correctly emitted Outcome C, exit code 1, and cleanup `true` when the runtime was absent;
- no remote Git operation, push, reset, force checkout, merge, rebase, or destructive cleanup was performed.

The full live second-server matrix remains explicitly unclaimed because the required InspIRCd runtime was unavailable on this host. The retained deterministic desktop smokes and both solution configurations were independently verified above.
