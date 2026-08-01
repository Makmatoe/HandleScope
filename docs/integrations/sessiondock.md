# SessionDock integration

[SessionDock](https://github.com/Makmatoe/SessionDock) is an optional,
standard-user client of HandleScope local API v1. The applications remain
separate repositories, downloads, installs, processes, and release channels.
HandleScope is not bundled inside SessionDock.

The supported integration is a narrow **post-launch** action. After Roblox has
started successfully, SessionDock must receive a positive launched process ID,
verify that it identifies a current-session `RobloxPlayerBeta.exe`, and derive
the session-specific `ROBLOX_singletonEvent` path from that process. It first
asks HandleScope to close the exact event in that launched PID. Only after that
PID-scoped close succeeds may it perform the separately authenticated,
name-based sweep of other verified Roblox Player processes. HandleScope is not
a general process-control extension point.

## User-control boundary

SessionDock may call HandleScope only after the user explicitly enables the
integration and a running API has published:

```text
%LOCALAPPDATA%\HandleScope\connection.json
```

The API must therefore be running before the Roblox launch if the post-launch
action is expected to run. HandleScope remains optional and SessionDock must
never embed its files, elevate it, uninstall it, downgrade it, silently change
it, or make a Roblox launch depend on it.

Starting with HandleScope v0.1.4, a compatible release published from the
canonical `Makmatoe/SessionDock` repository may offer a managed setup only when
all of these controls are present:

1. A dedicated user action opens a confirmation that names the exact pinned
   HandleScope version and explains that continuing will download, install or
   replace the per-user API, start it immediately, and enable its limited
   per-user autostart task.
2. SessionDock pins one stable immutable `Makmatoe/HandleScope` release and the
   exact canonical Windows x64 package and checksum assets. It must reject a
   different version, repository, asset name, byte length, SHA-256 digest,
   checksum entry, or non-approved HTTPS download redirect. A missing
   `Content-Length` is acceptable only when the bounded stream ends at the exact
   pinned length and hash; a present contradictory length must be rejected.
3. Before any extracted file runs, SessionDock must enforce a bounded safe ZIP
   layout, cap entry count and total expanded bytes, reject filesystem links and
   unexpected entries, and verify the complete internal `CONTENTS.sha256`
   inventory. It must then run only the release's unmodified
   `api\Install-HandleScopeApi.ps1`, once with `-VerifyOnly` before the
   installation phase.
4. Both phases run as the current standard user. SessionDock may set
   `-ExecutionPolicy RemoteSigned` only for each verified child process so the
   local script can run when the effective default is `Restricted`. It must
   never use `Bypass` or `Unrestricted`, change a saved execution policy,
   override Group Policy, or request elevation.
5. The confirmed installation phase may pass `-StartNow -EnableAutostart` as
   disclosed. It must not pass `-EnableSessionDock`; the local SessionDock
   opt-in remains a separate explicit action after installation.
6. Every different HandleScope release requires a new reviewed pin and a new
   version-specific confirmation. SessionDock must not use a mutable latest
   download, silently update or retry an installation, or run other HandleScope
   start, stop, update, uninstall, or task-management commands.

The same confirmed flow may replace an older supported per-user installation
through HandleScope's own fail-closed installer. A user may always choose the
manual verified installation path instead.

SessionDock remains usable when HandleScope is absent, stopped, incompatible,
busy, or denies a request. Those conditions skip or fail only the optional
post-launch action; they do not undo or turn an otherwise successful Roblox
launch into a failed launch. The integration must never broaden the selector,
kill Roblox, or use an elevated fallback.

## Required client behavior

For every launch operation, SessionDock must:

1. Require the positive PID returned by the successful Roblox launch.
2. Verify that PID is a live, current-session `RobloxPlayerBeta` process and
   derive the exact Windows session number from it.
3. Read the connection document again; never cache its bearer token or port.
4. Reject a reparse-point connection file and accept only API version `v1`.
5. Accept only an absolute URL of the form `http://127.0.0.1:<port>` with no user
   info, non-root path, query, or fragment.
6. Confirm that the connection document's separate API PID names a live
   `HandleScope.Api` process.
7. Disable proxies, redirects, and cookies, and apply short timeouts and bounded
   response sizes.
8. Call `/v1/health` and require policy `roblox-singleton-event-v1` before
   sending the bearer token.
9. Send an exact-PID request with `allProcesses: false`. Require the successful
   dry-run response to contain a 43-character base64url `planId`, then send the
   identical selector with `dryRun: false` and that `planId` within five seconds.
   Never reuse a plan ID. The fixed bounded retry window exists only to allow
   the newly launched process to create its event.
10. Require the execution response to report the launched PID in `closed`, at
    least one closure, and no failures.
11. Only after step 10 succeeds, optionally read and validate a fresh connection
    document and perform a second dry-run/execution pair using the fixed process
    name and `allProcesses: true`.
12. Never persist, display, log, export, or send the bearer token or short-lived
    plan IDs to any other address.

SessionDock implements this HTTP v1 flow directly. The included
`Invoke-HandleScopeClose.ps1` client demonstrates the same discovery, health
validation, policy checks, safe HTTP options, and dry-run-before-execution
contract—including plan-ID binding—for manual use; SessionDock does not invoke
or copy that script.

## Explicit local setup

Use a normal, non-administrator PowerShell window. Choose commands from one of
the following locations; do not mix an extracted-bundle path with an installed
path.

### From an extracted release bundle

Run these commands from the root of the extracted HandleScope release:

```powershell
# Install the per-user API, start it, and explicitly opt SessionDock in.
.\api\Install-HandleScopeApi.ps1 -StartNow -EnableSessionDock
```

Add `-EnableAutostart` if the API should also start automatically at sign-in.
The install command creates the installed API location documented below.

### After the per-user API is installed

These absolute commands work only after installation:

```powershell
# Opt SessionDock in.
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Enable-SessionDockIntegration.ps1"

# Start an installed API that is not already running.
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Start-HandleScopeApi.ps1"
```

The opt-in helper writes only `%LOCALAPPDATA%\SessionDock\handlescope.json` with
this logical content:

```json
{
  "enabled": true
}
```

It does not start either application, access SessionDock account data, or copy
the API token or port. It performs an atomic, reparse-safe write. If the
canonical file is absent, the helper recognizes the former
`%LOCALAPPDATA%\RobloxOne\handlescope.json` only when it contains exactly this
minimal opt-in, then copies the opt-in without deleting or modifying legacy
data. A canonical setting always takes precedence. A non-minimal canonical
setting is never replaced without explicit `-Force`; with `-Force`, it is
replaced by exactly the minimal `enabled` setting shown above.

See [`../INSTALL.md`](../INSTALL.md) for installation, start/stop, and optional
autostart commands.

## Fixed command mapping

The following installed-client examples illustrate SessionDock's two request
selectors. They are diagnostic/manual equivalents, not commands SessionDock
runs. Replace `1234` with the PID returned by the successful Roblox launch:

```powershell
$launchedPid = 1234
$sessionId = (Get-Process -Id $launchedPid -ErrorAction Stop).SessionId
$eventPath = "\Sessions\$sessionId\BaseNamedObjects\ROBLOX_singletonEvent"

# Required first operation: exact launched PID only.
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Invoke-HandleScopeClose.ps1" `
  -ProcessId $launchedPid `
  -HandleName $eventPath `
  -Type 'Event' `
  -Access '0x001F0003' `
  -Exact

# Optional second operation, only after the PID-scoped close succeeds.
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Invoke-HandleScopeClose.ps1" `
  -ProcessName 'RobloxPlayerBeta' `
  -HandleName $eventPath `
  -Type 'Event' `
  -Access '0x001F0003' `
  -Exact `
  -AllProcesses
```

Every field remains fixed to the compiled policy:

| Operation | Process selector | `allProcesses` | Handle selector |
| --- | --- | --- | --- |
| Required first close | Exact launched PID | `false` | Launched process session's exact `ROBLOX_singletonEvent` path |
| Optional follow-up sweep | `RobloxPlayerBeta` | `true` | The same exact session-specific event path |

Both operations use type `Event`, access `0x001F0003`, exact matching, and
`closeAll: false`. The SessionDock `allProcesses` setting enables the optional
follow-up sweep; it never broadens the required first PID-scoped request.

Do not store a PID, raw handle value, bearer token, API port, or hard-coded
Windows session number. PIDs and handles are reusable, and the event path must
be derived from the verified launched process at operation time. Do not expose
`closeAll`, partial matching, arbitrary process names, object types, access
masks, or paths as integration settings; the server rejects them.

## Failure behavior

Treat a missing or stale connection file, unavailable launched PID, unexpected
health policy, Roblox executable trust or HandleScope install problem, `401`,
`403`, `404`, `409`, `429`, timeout, or malformed response as a denied optional
post-launch operation. Do not fall back to killing Roblox, closing a raw handle,
using an administrator helper, or invoking an unrelated HandleScope copy.

The PID-scoped operation succeeds only when execution returns `200`, reports at
least one closure, includes the launched PID in `closed`, and reports no
failures. The optional all-process sweep is a separate result and must not be
used to claim that the PID-scoped operation succeeded. A HandleScope failure
must not be reported as failure of the Roblox process that already launched.

See [`../../API.md`](../../API.md) for the exact request contract and
[`../THREAT_MODEL.md`](../THREAT_MODEL.md) for residual same-user risks.
