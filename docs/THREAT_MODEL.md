# Threat model

## Status and security boundary

HandleScope can duplicate and close handles in other Windows processes. A
mistaken or malicious close can destabilize the target even when no Windows
privilege boundary is crossed.

The desktop application, headless API, and native setup tool run as the invoking
standard user (`asInvoker`) and do not enable `SeDebugPrivilege`. Setup also
rejects elevated, service-account, and session-0 execution. The desktop exposes
an interactive, confirmed close operation only for non-elevated processes owned
by the same Windows SID in the same interactive session.

The API is a narrower boundary. It refuses to start with an elevated token, as
a Windows service account, or in session 0. It accepts only the compiled
`roblox-singleton-event-v1` recipe for a trusted, same-user, same-session,
non-elevated `RobloxPlayerBeta.exe`. It is not a general handle broker and has
no configuration that expands that policy.

Standard-user execution materially limits impact, but it does not isolate
HandleScope from other software running as the same user. Loopback and a bearer
token protect against remote access and accidental calls; they do not establish
the identity or intent of another same-user process.

## Protected assets

- Integrity and availability of processes whose handles can be inspected or
  closed.
- User intent: a close must affect only the process instance and object the user
  reviewed, or the exact compiled automation recipe.
- Confidentiality of handle names, local paths, process metadata, session
  identifiers, and the rotating API bearer token.
- Integrity of the standalone API/setup executables, compatibility scripts,
  optional scheduled task and discovery document; and integrity of the
  SessionDock-embedded source/provenance, parent/pipe bootstrap, and release
  pipeline.
- Availability of the user session and Roblox client during automation.

## Trust boundaries

1. A standalone user downloads a HandleScope release from GitHub, verifies its
   repository-bound attestation and checksums, then unblocks and extracts it
   locally. The native setup executable validates the exact internal inventory
   before installation; it does not use PowerShell.
2. SessionDock 3.0 synchronizes reviewed HandleScope 0.3.0 source from an
   immutable tag/commit, records its allowlisted paths and hashes in
   `SessionDock.HandleScope/handlescope-upstream.json`, and compiles it into
   `SessionDock.exe`. At runtime SessionDock creates one non-elevated child,
   transfers bootstrap data through an inherited anonymous pipe, and owns the
   child's lifetime.
3. The user drives the desktop UI, which crosses into Windows process and native
   handle APIs using that user's existing access token.
4. A standalone client reads `%LOCALAPPDATA%\HandleScope\connection.json`; the
   SessionDock parent instead retains its child's endpoint/token in memory. Both
   send authenticated HTTP only to an ephemeral IPv4 loopback port.
5. The API validates the exact request, resolves current process identity and
   executable trust, then uses Windows native APIs to inspect and close the
   approved event.
6. For standalone use only, an optional per-user scheduled task launches the API
   at limited privilege for that Windows SID at interactive logon. Included mode
   creates no task and exits with its SessionDock parent.

## API policy invariant

The public API can close only an `Event` named exactly
`\Sessions\<API-session>\BaseNamedObjects\ROBLOX_singletonEvent` with granted
access `0x001F0003`, in a verified `RobloxPlayerBeta.exe` owned by the API user
in the same Windows session. A name-based request must explicitly select all
matching Roblox processes; a PID request selects one. Raw handle values,
partial name matches, arbitrary types or access masks, and `closeAll` are
denied. Candidate process count is capped at 32, and each approved process must
have exactly one matching event.

The executable verifier resolves the image's canonical path from an open file
handle, rejects reparse points, permits only a Roblox
`Versions\version-*\RobloxPlayerBeta.exe` layout under known installation
roots, checks version-resource identity, and requires a trusted Authenticode
signature whose organization is `Roblox Corporation`.

## Principal threats and mitigations

| Threat | Current mitigation | Residual risk |
| --- | --- | --- |
| A remote host reaches the API | Kestrel binds to `127.0.0.1`; requests must also present the exact loopback host | A local networking or OS compromise is outside this control |
| A browser page triggers a close | Authenticated endpoints reject `Origin`, `Referer`, and `Sec-Fetch-Site` headers; no token is exposed to web content | Browser filtering is defense in depth and is not a caller identity mechanism |
| Token guessing or accidental disclosure | A fresh cryptographically random 256-bit token is protected by a current-user-only discovery ACL in standalone mode; included mode transfers it through an inherited pipe and keeps it only in parent/child memory. Responses and minimal logs omit it | Malware running as the same user can generally access same-user files or process memory |
| A stolen token becomes a general process-control capability | The compiled policy permits only the exact Roblox singleton event and has no arbitrary inspect endpoints | A same-user caller can still disrupt Roblox or exhaust bounded operation/plan capacity |
| Service, administrator, or cross-session misuse | Runtime startup rejects elevated tokens, service SIDs, and session 0; targets must match the API's SID/session and be non-elevated | A hostile local administrator can bypass user-mode controls |
| A lookalike process is selected by name | The API verifies owner, session, elevation, canonical installation layout, reparse points, version identity, trusted signature, and signer organization | Trust ultimately depends on Windows code-signing and filesystem guarantees; verification is cache-only to prevent network access and therefore cannot fetch fresh revocation data |
| Stale PID or process replacement | Process creation time, SID, session, image path, and elevation state are captured and revalidated before close | Windows does not provide one transaction covering every check and closure |
| Stale or recycled handle | The engine rechecks kernel-object identity, handle value, object address, access mask, and snapshot metadata before `DuplicateHandle(..., DUPLICATE_CLOSE_SOURCE)` | Windows exposes no atomic compare-and-close primitive |
| Broad, malformed, or resource-heavy requests | An 8 KiB strict JSON reader accepts only the exact `application/json` media type and rejects unknown, duplicate, incorrectly cased, or malformed fields; process caps apply after authorization; Kestrel limits headers, timeouts, connections, and upgrades | Repeated same-user requests can still cause bounded local work |
| Close without review or request replay | Each successful dry run receives a cryptographically random plan ID bound to its canonical request; execution must present both, and the plan expires after five monotonic seconds and is consumed once | A same-user attacker with the token can create enough plans or operations to cause bounded denial of service; a disclosed plan ID can be raced and consumed |
| Concurrent destructive requests | A single-operation gate rejects overlap with `429` | Callers may retry and cause local denial of service |
| Tampered standalone discovery/runtime or included bootstrap | Standalone directories reject reparse points and use protected ACLs; clients validate URL, token shape, API PID/name, version, and policy. Included mode has no discovery file: it verifies the exact parent/child relationship and uses an inherited pipe plus in-memory endpoint/token | Same-user malware can still inspect or change same-user process memory/state |
| Standalone installer substitution, race, metadata-stream injection, or downgrade | Native setup admits only the fixed eleven-file API source set; locks and verifies the manifest, unnamed source data, and directory identities; permits only bounded untrusted source metadata streams; validates `Zone.Identifier`; hashes and copies only unnamed bytes; requires stream-clean staged and installed files; recovers its bounded swap transaction; and blocks downgrade by default | Named source streams are not authenticated content and are never copied. Internal hashes do not authenticate origin because a malicious bundle could replace both setup and its manifest; standalone users must first verify the GitHub attestation and external ZIP checksum. An explicit manual `--allow-downgrade` bypasses version ordering |
| PowerShell policy is weakened to make setup run | The recommended setup, start, stop, opt-in, and uninstall commands are fixed native commands; compatibility scripts remain optional wrappers only. Setup never launches PowerShell or changes policy | `MachinePolicy`, `UserPolicy`, WDAC, AppLocker, Smart App Control, and antivirus may still block execution; HandleScope does not bypass them |
| SessionDock's embedded copy drifts or is substituted | The synchronized source is allowlisted and hash-bound to an immutable upstream tag/commit in provenance; CI/release gates verify version, inventory, license, SBOM, and that HandleScope exists only inside approved `SessionDock.exe` bytes | The SessionDock repository, build/release pipeline, and maintainers become additional trusted inputs |
| SessionDock leaks or orphans its included token/child | Bootstrap uses an inherited anonymous pipe, secrets remain in memory, the child verifies its parent, and parent lifetime/disable triggers shutdown; no command-line, environment, file, service, or task secret/lifecycle channel exists | Same-user malware can inspect process memory; OS termination can briefly race cleanup |
| SessionDock modifies standalone HandleScope | Runtime-source separation forbids SessionDock download/install/start/stop/update/downgrade/reconfiguration/uninstall actions; advanced mode is connection-only | A compromised SessionDock process already has the current user's authority and can attempt out-of-contract actions |
| Scheduled-task persistence is widened | Standalone autostart is opt-in, per-SID, interactive-logon only, and `RunLevel Limited`; install/uninstall validate the expected action and privilege level. Included mode creates no task | The owning user can modify their own standalone limited task |
| Supply-chain substitution | Restore is locked; third-party package references are prohibited; workflow actions are pinned; release publication is environment-approved and fresh-only; exact source provenance, catalogs, API/setup identities, checksums, SPDX inventory, redownload verification, immutable releases, and GitHub attestations are required | GitHub, both repository administrations, Actions, and maintainer accounts remain trusted dependencies; the free release model provides no Windows publisher identity and reputation systems may block a new unsigned hash |
| Sensitive data enters diagnostics | No telemetry exists; API logs contain only bounded lifecycle and generic failure data; HTTP errors omit exception details and raw native identifiers | Desktop screenshots and manually copied output can still expose local names or paths |

## Desktop-specific considerations

The desktop can inspect more handle types than the API, but only in processes
that pass its same-SID, same-session, and non-elevated filter. It pins the
process creation time, refreshes after identity changes, requires a selected
scan result, shows a destructive-action warning, and revalidates identity before
closure. The recurring-command button is separately restricted to the compiled
Roblox recipe.

The desktop remains a sharp tool. A user can deliberately close a different
same-user process handle after confirmation. That capability is intentional and
is why the desktop is not suitable for unattended automation.

## Release gates

A public release is acceptable only when all release automation remains
fail-closed and the produced assets pass independent verification:

- the version and stable `v*` tag match and the annotated tag points to
  protected `main`;
- locked restore, repository checks, build, and controlled integration tests
  pass without weakening exclusions;
- the protected release environment admits only the reviewed tag-triggered
  publication job and requires no secret or external signing account;
- the final ZIP inventory, native setup/API identities, runtime and external
  manifest capabilities, SHA-256 checksums, SPDX SBOM, GitHub provenance, and
  downloaded draft assets all verify before publication;
- a SessionDock release that includes this engine independently verifies the
  immutable upstream tag/commit, synchronized allowlist/hashes, version,
  license/notices/SBOM, parent/pipe lifecycle, and absence of any HandleScope
  publish sidecar; and
- release notes accurately disclose the destructive behavior, supported policy,
  privacy behavior, and known limitations.

No local flag may convert a failed build, inventory, checksum, SBOM,
provenance, or release-state verification into a public release.

## Out of scope and accepted limitations

- Making arbitrary hostile local-administrator or kernel activity safe.
- Isolating the API from malware already executing as the same Windows user.
- Access to elevated, cross-user, cross-session, protected-process, or Protected
  Process Light targets that Windows denies.
- Guaranteeing Roblox or another target remains correct after a handle is
  forcibly closed.
- Guaranteeing compatibility if Windows changes the internal system-handle
  information class used for discovery.
- Overriding an organization's PowerShell, application-control, SmartScreen,
  Smart App Control, or antivirus policy, or guaranteeing reputation acceptance
  for unsigned binaries.

These exclusions do not broaden the API policy or justify running HandleScope
with administrator rights.
