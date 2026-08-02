# HandleScope

HandleScope is a Windows x64 handle-inspection tool with two independent parts:

- `desktop\HandleScope.exe`: a portable WPF browser for inspecting and closing
  named handles in accessible, same-user processes;
- `api\HandleScope.Api.exe`: a loopback API that can automate only HandleScope's
  compiled Roblox singleton-event policy.

`api\HandleScope.Setup.exe` is the recommended verifier, installer, and
lifecycle tool. It is a native executable, so it works when PowerShell script
execution is restricted. [SessionDock](https://github.com/Makmatoe/SessionDock)
3.0 also ships the reviewed HandleScope 0.3.0 engine inside `SessionDock.exe`.
SessionDock users do not need this repository's ZIP, API installer, PowerShell
scripts, UAC, scheduled task, or autostart setup.

> [!CAUTION]
> Closing a handle can crash a process, corrupt its state, or lose unsaved data.
> Close only a handle whose purpose and impact you understand.

## Choose SessionDock or standalone HandleScope

- If you use SessionDock 3.0 or later, install only SessionDock and select
  **Included with SessionDock (recommended)**. SessionDock owns the child
  lifetime and keeps its loopback token in an inherited pipe/in memory.
- Use the instructions below only for HandleScope's portable desktop, direct API
  clients, or the SessionDock **Standalone HandleScope (advanced)** source.
- A standalone installation remains an independent product. SessionDock never
  installs, updates, starts, stops, reconfigures, or uninstalls it.

## Install the API

This section is for direct standalone users. It is not a prerequisite for
SessionDock 3.0's included engine.

Official releases support Windows 10 and 11 on x64, include their own .NET
runtime, and run from a normal, non-administrator terminal.

1. Download the ZIP and its matching verification assets from the
   [latest official release](https://github.com/Makmatoe/HandleScope/releases/latest),
   never from an issue, chat attachment, mirror, or Actions artifact.
2. Before running anything, use
   [Verify a HandleScope download](docs/VERIFY_DOWNLOAD.md) to check GitHub
   provenance and SHA-256 values.
3. Extract the complete ZIP into a new, local, non-linked folder.
4. Open the extracted `HandleScope-<version>-win-x64` folder in a normal
   terminal and verify its fixed eleven-file API inventory:

   ```powershell
   .\api\HandleScope.Setup.exe verify
   ```

5. Continue only if verification succeeds. Choose an install command:

   ```powershell
   # Install and start now.
   .\api\HandleScope.Setup.exe install --start-now

   # Also start at sign-in.
   .\api\HandleScope.Setup.exe install --start-now --enable-autostart

   # Also explicitly opt SessionDock in.
   .\api\HandleScope.Setup.exe install --start-now --enable-autostart --enable-sessiondock
   ```

The API installs at `%LOCALAPPDATA%\Programs\HandleScope\Api`. Autostart uses a
current-user, limited interactive-logon task. Setup never requests UAC, launches
PowerShell, or changes execution policy. The full safety model is in
[Install HandleScope](docs/INSTALL.md).

## Run the portable desktop

The desktop does not require the API or installation:

```powershell
.\desktop\HandleScope.exe
```

Filter by executable name or PID, select a process, enter a handle-name fragment
(or leave it empty), choose **Contains** or **Exact name**, and select **Scan
handles**. Review every field and confirmation before closing anything.

Targets are limited to accessible, non-elevated, same-user/session processes;
System PID 4 and HandleScope itself are blocked. **Copy recurring command** is
available only for the supported Roblox singleton event.

## Start, stop, update, or uninstall

Use the installed native setup tool:

```powershell
$setup = "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe"
& $setup start
& $setup stop
```

To update, download and verify the complete newer release, close the desktop,
then run from the newer extracted folder:

```powershell
.\api\HandleScope.Setup.exe verify
.\api\HandleScope.Setup.exe install --start-now
```

An existing autostart task stays enabled. Downgrades fail unless an intentional,
verified recovery uses `--allow-downgrade`.

To uninstall the API, its expected autostart task, and its runtime data:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe" uninstall
```

Add `--keep-diagnostics` only to retain the bounded local log. Close the desktop
and delete its extracted folder to remove it.

## SessionDock integration and version selection

SessionDock 3.0 offers two version sources and a separate API selector:

| SessionDock source | Behavior |
| --- | --- |
| **Included with SessionDock (recommended)** | Uses HandleScope engine 0.3.0 compiled into `SessionDock.exe`; no separate HandleScope download or lifecycle setup. |
| **Standalone HandleScope (advanced)** | Connects to an already installed and running standalone API; SessionDock does not change it. |

The API selector is **Automatic**, `v2`, or `v1`. It chooses only a compiled
protocol contract, never a download or installed-package version. The included
engine version follows the verified SessionDock release.

For normal included mode, open SessionDock's Integrations panel, keep the
recommended source, select an API preference, then select **Enable**. SessionDock
checks readiness automatically; wait for **Ready** or use **Retry** after a
bounded failure. No command in this repository is needed.

For the advanced standalone source only, opt in during standalone installation
with `--enable-sessiondock`, or later with:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe" enable-sessiondock
```

This writes only `%LOCALAPPDATA%\SessionDock\handlescope.json`; it starts
nothing and accesses no token or account data. A non-minimal setting is replaced
only after review and an explicit `--force`.

- Fresh SessionDock 3.0 setups default to the included engine. During upgrade,
  an old Keep installed/Exact choice or an enabled Automatic setup with a
  verified running standalone API is migrated to the advanced source. Existing
  `handlescope.json` opt-ins remain compatible, and the standalone installation
  is never modified.
- SessionDock 2.9.x retains its older signed-catalog/standalone installation
  flow. Upgrade SessionDock to remove that separate-install requirement.
- SessionDock 2.8.x remains on its authenticated HandleScope 0.2.2 path.
- HandleScope's desktop **SessionDock API** selector chooses only the protocol
  preference—automatic/v2 or legacy v1—not the installed package version. Both
  API families remain available, and the preference applies after API restart.

The signed compatibility catalog remains available for older SessionDock
clients and reviewed advanced-standalone identities; the 3.0 included flow does
not download or execute from it. See the
[SessionDock integration contract](docs/integrations/sessiondock.md).

## Fix common installation problems

These fixes apply only to a direct standalone HandleScope download. SessionDock
3.0 users should install/verify SessionDock itself and use its included source;
they should not work around a blocked HandleScope ZIP or script.

### “Running scripts is disabled on this system”

PowerShell blocked a legacy `.ps1` before it read any arguments. Stop invoking
`Install-HandleScopeApi.ps1` and run the native executable:

```powershell
.\api\HandleScope.Setup.exe verify
.\api\HandleScope.Setup.exe install --start-now --enable-autostart
```

`-ExecutionPolicy` is a `powershell.exe` startup option, not a standalone
command or installer argument. Appending `-ExecutionPolicy Bypass`,
`--ExecutionPolicy`, or `--ExecutionBypass` cannot make a blocked script start.
No execution-policy change is needed for native setup.

The seven `.ps1` files remain for backwards compatibility, but policy can still
block them. Use `HandleScope.Setup.exe` for new automation. For verified 0.1.x
or 0.2.x recovery, follow the
[legacy guidance](docs/INSTALL.md#legacy-powershell-compatibility); never use
`Bypass`.

### The browser says “Virus scan failed”

No HandleScope code has run. The downloading device's browser/security-product
scan failed or refused the file; this is separate from PowerShell policy. The
same asset can work on one PC and fail on another because their security
products, policies, reputation state, or scanner health differ.

1. Confirm the canonical GitHub release URL.
2. Check browser download details and Windows Security protection history.
3. Update security intelligence and follow normal remediation guidance.
4. On a managed device, give the administrator the asset name and SHA-256.

Do not disable antivirus, SmartScreen, Smart App Control, WDAC, AppLocker, or
organization policy, and do not add a broad exclusion.

### “Unknown publisher,” a security block, or failed verification

HandleScope is intentionally not Authenticode-signed because the project uses
no paid certificate or signing service. GitHub attestations and hashes verify
the published bytes; they do not create publisher reputation or override device
policy. Follow the [verification guide](docs/VERIFY_DOWNLOAD.md) and the device
owner's normal approval process.

If native `verify` fails, stop. Re-download, re-verify, and extract into a new
empty folder; never move individual API files, regenerate `CONTENTS.sha256`, or
mix versions. For an old `HandleScope Local API` task, use its verified matching
uninstaller or remove only the exact confirmed task.

For startup failures, run the installed `start` command and follow its recovery
message. The bounded log is `%LOCALAPPDATA%\HandleScope\api.log`. Never share
`connection.json`; it contains the live bearer token.

## Security and privacy

The desktop, API, and setup tool run as the current standard user (`asInvoker`),
do not enable `SeDebugPrivilege`, and do not request administrator rights.

The API accepts only `roblox-singleton-event-v1`: the exact current-session
`ROBLOX_singletonEvent` with access `0x001F0003` in a trusted, same-user/session
`RobloxPlayerBeta.exe`. A close requires a dry run and the identical request
with its single-use plan ID within five seconds.

The standalone API binds to an ephemeral IPv4 loopback port and stores its
rotating 256-bit token in protected
`%LOCALAPPDATA%\HandleScope\connection.json`. The SessionDock-included engine
uses the same loopback policy but receives its token through an inherited pipe
and keeps it in parent/child memory; it creates no connection file. Both refuse
elevated, service-account, and session-0 execution. HandleScope has no telemetry,
analytics, advertising, crash upload, cloud sync, or silent updater.

Windows has no atomic compare-and-close operation, so a small handle-recycling
race remains. Read [Security](SECURITY.md), [Privacy](PRIVACY.md), the
[threat model](docs/THREAT_MODEL.md), and the [API reference](API.md) before
integrating or changing the project.

## Build and test

Source work requires Windows, PowerShell, and the exact .NET SDK in
[`global.json`](global.json). The six .NET 10 Windows projects have no
third-party NuGet package references. Run the complete local gate:

```powershell
.\scripts\Build.ps1 -CI
```

It checks hygiene and PowerShell compatibility, restores locked dependencies,
builds with warnings as errors, and runs setup tests plus a controlled harness
that touches only its own child process, file, and event. Use
`-SkipControlledIntegration` only for a documentation/environment limitation.
See [Releasing HandleScope](docs/RELEASING.md) for publication gates.

The SessionDock repository synchronizes its included component from an
immutable HandleScope tag into `SessionDock.HandleScope/Upstream/` and records
the tag, commit, version, file allowlist, and hashes in
`SessionDock.HandleScope/handlescope-upstream.json`. Security fixes affecting the included
engine must be coordinated here first, then synchronized and documented in
SessionDock; do not maintain an untracked downstream fork.

## License

HandleScope is available under the [MIT License](LICENSE.md). Bundled .NET
runtime components retain their own terms, summarized in
[Third-party notices](THIRD_PARTY_NOTICES.md).
