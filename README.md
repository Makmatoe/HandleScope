# HandleScope

HandleScope is a Windows handle inspection tool with a narrowly scoped local
automation API. It contains:

- `HandleScope.exe`, a WPF browser for reviewing and closing named handles in
  non-elevated processes owned by the current user in the current Windows
  session;
- `HandleScope.Api.exe`, an independent loopback API whose release policy can
  close only Roblox's exact session singleton event;
- `HandleScope.Core`, the shared Windows process and native-handle engine;
- a controlled integration harness that operates only on a child process it
  creates.

Both applications run as the current standard user (`asInvoker`). They do not
request administrator rights or enable `SeDebugPrivilege`. The desktop does not
depend on the API, and the API does not open or depend on the desktop.
[SessionDock](https://github.com/Makmatoe/SessionDock) is a separate, optional
client and is not bundled here.

Process Explorer is an interactive Sysinternals application rather than a
public automation API. HandleScope does not bundle or require Sysinternals. It
discovers the Windows system handle table and uses the documented
[`DuplicateHandle` close-source operation](https://learn.microsoft.com/windows/win32/api/handleapi/nf-handleapi-duplicatehandle).

## Safety first

Closing a handle can crash a target process, corrupt its state, or cause data
loss. Keep unsaved work closed and act only on processes you own and can safely
restart.

The desktop limits its process list to non-elevated processes owned by the same
Windows user in the same interactive session. It blocks Windows System PID 4
and its own process, pins the target process instance, rechecks the kernel-object
identity before closing, and asks for confirmation. These controls reduce risk;
they do not make arbitrary handle closure safe.

The local API is more restrictive. It accepts only the compiled
`roblox-singleton-event-v1` policy: a trusted `RobloxPlayerBeta.exe` owned by
the same user in the same session and the exact
`\Sessions\<current-session>\BaseNamedObjects\ROBLOX_singletonEvent` event with
access mask `0x001F0003`. Every close requires a successful dry run followed by
an identical, single-use execution request within five seconds.

Windows does not expose an atomic compare-and-close operation, so a small race
remains when a process rapidly recycles handles. Protected, elevated,
cross-user, and cross-session targets are intentionally unavailable. Handle
discovery also uses the internal `NtQuerySystemInformation` system-handle class,
which Windows may change.

Read the [security policy](SECURITY.md), [privacy notice](PRIVACY.md), and
[threat model](docs/THREAT_MODEL.md) before running or changing the project.

## Install or run

Official Windows x64 releases are self-contained and do not require a separate
.NET installation. Download the complete ZIP, SPDX SBOM, and checksum file from
the [latest GitHub release](https://github.com/Makmatoe/HandleScope/releases/latest), then verify them using
[`docs/VERIFY_DOWNLOAD.md`](docs/VERIFY_DOWNLOAD.md), and extract it before
running anything.

- Run `desktop\HandleScope.exe` directly for interactive inspection. It is a
  portable application.
- From a normal, non-administrator PowerShell window, run
  `api\Install-HandleScopeApi.ps1 -StartNow -EnableAutostart -EnableSessionDock`
  for the easiest complete setup. Omit either opt-in switch when it is not
  wanted.
  The API is installed for the current user under
  `%LOCALAPPDATA%\Programs\HandleScope\Api`.

The installer verifies the complete API inventory and every internal manifest
hash, then removes inherited Windows download markers only from those verified
installed copies. It does not weaken PowerShell execution policy or request UAC
approval. Releases are intentionally not
Authenticode-signed because this project uses no paid certificate or signing
service; authenticity instead comes from the GitHub release source, SHA-256
manifest, and GitHub artifact attestation. Windows may therefore show
**Unknown publisher** or a SmartScreen warning. See
[`docs/INSTALL.md`](docs/INSTALL.md) for the full install, verification,
SessionDock connection, update, and uninstall workflow.

## Desktop workflow

1. Start `desktop\HandleScope.exe` as your normal Windows user.
2. Filter by executable name or PID and select an available process.
3. Enter a handle-name fragment, or leave it empty to show resolvable named
   handles.
4. Select **Contains** or **Exact name**, then choose **Scan handles**.
5. Review the object type, handle value, access mask, and resolved name.
6. Close only a result whose impact you understand.

The **Copy recurring command** action is enabled only for the supported Roblox
singleton event. Its output calls the installed API client with the exact
process name, session-specific event path, object type, and access mask. It
does not store a PID or raw handle value.

## Local API boundary

The API binds to an ephemeral IPv4 loopback port and publishes its current URL,
process ID, and rotating 256-bit bearer token in the current user's protected
`%LOCALAPPDATA%\HandleScope\connection.json`. Clients must read and validate
that file for every operation; they must never hard-code the port, retain the
token, follow redirects, use a proxy, or send the token off-machine.

The API refuses to start under an elevated token, a Windows service account, or
session 0. It exposes only health, strict Roblox singleton close, and
authenticated shutdown endpoints. See [`API.md`](API.md) for the exact v1
contract and [`docs/integrations/sessiondock.md`](docs/integrations/sessiondock.md)
for the SessionDock client boundary.

The recommended install command above explicitly opts SessionDock in. To enable
the integration separately later, run:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Enable-SessionDockIntegration.ps1"
```

That helper writes only `%LOCALAPPDATA%\SessionDock\handlescope.json` with the
local `enabled` flag. If the canonical file is absent, it can safely copy the
minimal opt-in written to the former `%LOCALAPPDATA%\RobloxOne` location by an
older release; the legacy file is left untouched. A canonical setting always
wins and is never replaced by legacy state. The helper does not start either
application, copy a token, or modify account data, and it refuses to replace a
non-minimal canonical setting unless the user explicitly re-runs it with
`-Force`.

## Repository layout

| Path | Purpose |
| --- | --- |
| `HandleScope/` | WPF desktop inspector |
| `HandleScope.Core/` | Windows handle and process engine |
| `HandleScope.Api/` | Restricted local API and per-user lifecycle scripts |
| `HandleScope.IntegrationTests/` | Controlled child-process harness |
| `docs/` | Installation, security, release, and integration guidance |
| `scripts/` | Repository, build, packaging, and release verification |

## Build and validate

Requirements are Windows 10 or 11, PowerShell, and the .NET SDK selected by
[`global.json`](global.json). The projects target .NET 10 for Windows and use
no third-party NuGet packages.

Run the complete local verification:

```powershell
.\scripts\Build.ps1 -CI
```

This verifies repository hygiene, restores locked dependencies, builds all four
projects with warnings treated as errors, and runs the controlled integration
harness. The harness creates its own process, file, and named event; it must not
be changed to target unrelated processes.

Use the skip switch only when a documentation or environment limitation makes
the controlled harness inapplicable:

```powershell
.\scripts\Build.ps1 -CI -SkipControlledIntegration
```

Release publication is fail-closed: it requires a version-matched annotated tag
at protected `main`, pinned CI actions, locked restore, the controlled test
harness, exact file inventories, checksums, an SPDX SBOM, and GitHub build
provenance. No certificate, paid signing service, or repository secret is
required. HandleScope has no in-app updater; users choose when to download,
verify, and install a newer release.

## License

HandleScope is available under the [MIT License](LICENSE.md). Bundled .NET
runtime components retain their own terms, included in every release and
summarized in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
