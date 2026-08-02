# SessionDock integration

[SessionDock](https://github.com/Makmatoe/SessionDock) is an optional,
standard-user client of HandleScope local API v1 and v2. SessionDock 3.0 ships
the reviewed HandleScope 0.3.0 engine inside `SessionDock.exe` while HandleScope
continues as an independent repository and standalone release. Existing direct
clients retain the v1 discovery, health, and close contracts.

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
integration. SessionDock 3.0 exposes these independent choices:

| Selector | Choice | Boundary |
| --- | --- | --- |
| Runtime source | **Included with SessionDock (recommended)** | HandleScope 0.3.0 is compiled into `SessionDock.exe`. SessionDock starts one non-elevated, parent-owned child and supplies bootstrap data through an inherited anonymous pipe. |
| Runtime source | **Standalone HandleScope (advanced)** | SessionDock connects to an already installed/running standalone API through `%LOCALAPPDATA%\HandleScope\connection.json` and never mutates its lifecycle. |
| Standalone runtime version | **Automatic** | Accepts any installed runtime authorized by the signed catalog and compatible with this SessionDock version. |
| Standalone runtime version | **Keep the installed version** | Accepts the installed reviewed compatible runtime without requesting a replacement or lifecycle action. |
| Standalone runtime version | Exact reviewed version | Requires the already running runtime to match that exact signed-catalog-reviewed compatible version; it never fetches or installs it. |
| API | **Automatic**, `v2`, or `v1` | Chooses only an operation adapter compiled into SessionDock; it never selects or downloads a package. |

The standalone runtime-version selector is shown only for the advanced source.
A stale exact pin remains visible even when it is absent from the current
reviewed list or does not match the running runtime. The user can recover by
selecting **Automatic**, **Keep the installed version**, or another reviewed
exact version without changing any installed software.

Opening the integration panel remains local-only. **Refresh reviewed versions**
is a standalone-only, explicit network action. It fetches the latest catalog
from SessionDock's canonical GitHub release URL and accepts it only after the
existing signature, product/repository/key identity, validity-window,
compatibility, and rollback-floor checks pass. Refresh preserves the selected
runtime source, standalone version, and API preference. It never downloads,
installs, starts, stops, updates, downgrades, reconfigures, or uninstalls either
runtime.

Included mode must satisfy every control below:

1. The synchronized source is pinned to an immutable HandleScope tag and commit
   by `SessionDock.HandleScope/handlescope-upstream.json`, including an allowlisted file
   inventory and hashes.
2. The engine is part of the verified `SessionDock.exe` bytes. No HandleScope
   executable, installer, script, component directory, service, scheduled task,
   autostart entry, or separate updater is published or created.
3. The child verifies its exact current-user/current-session parent, refuses
   elevation/service/session 0, binds only to ephemeral numeric IPv4 loopback,
   and exits when disabled or when the parent lifetime ends.
4. The rotating token is transferred through the inherited pipe and remains in
   parent/child memory. It is never written to disk, a command line, environment
   variable, preference, log, diagnostics, export, or UI.
5. SessionDock authenticates metadata and health before an operation and exposes
   only the fixed `roblox-singleton-event-v1` policy and compiled v1/v2 routes.

Advanced standalone mode must satisfy every control below:

1. The user installs, starts, updates, and removes HandleScope independently.
2. SessionDock never downloads, installs, starts, stops, updates, downgrades,
   reconfigures, or uninstalls it, and never changes its scheduled task or API
   compatibility preference.
3. SessionDock strictly validates the protected discovery file and
   same-user/same-session process before sending its bearer token only to the
   numeric-loopback API.
4. Authenticated metadata must identify a compatible reviewed runtime and can
   select only a locally compiled adapter and the fixed policy.

The signed compatibility catalog remains available for older SessionDock
clients and reviewed advanced-standalone identities. It remains authorization
data rather than executable policy and cannot define a path, command, argument,
endpoint, parser, or new capability. SessionDock 3.0's included flow never
downloads or executes from it; the removed in-app downloader/installer must not
be reintroduced.

For backwards compatibility, the first 3.0 run preserves an old Keep
installed/Exact selection as standalone. An enabled Automatic setup also stays
standalone when its current API passes the bounded migration probe. A fresh or
otherwise unselected setup uses included mode. SessionDock then stores the
explicit source without changing any standalone file, process, or task.

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
3. For included mode, obtain the endpoint and token only from the authenticated
   in-memory parent/child bootstrap state; verify the exact owned child and never
   create or consult `connection.json`.
4. For standalone mode, re-read the connection document for every operation;
   reject a reparse-point file, require the exact five-field v1 discovery
   schema, and never cache its bearer token or port.
5. Accept only an absolute URL of the form `http://127.0.0.1:<port>` with no user
   info, non-root path, query, or fragment.
6. Confirm the runtime PID is the exact live included child or the validated
   same-user/same-session standalone `HandleScope.Api` process selected by the
   user.
7. Disable proxies, redirects, and cookies, and apply short timeouts and bounded
   response sizes.
8. Call `/v1/health` and require its exact shape and policy
   `roblox-singleton-event-v1` before any close request.
9. Authenticate `/v1/metadata`, require its exact schema, cross-check reported
   product version, protocol list, policy, and capabilities against the selected
   source identity, then select only the compiled `/v1` or `/v2` adapter.
10. Send an exact-PID request with `allProcesses: false`. Require the successful
   dry-run response to contain a 43-character base64url `planId`, then send the
   identical selector with `dryRun: false` and that `planId` within five seconds.
   Never reuse a plan ID. The fixed bounded retry window exists only to allow
   the newly launched process to create its event.
11. Require the execution response to report the launched PID in `closed`, at
    least one closure, and no failures.
12. Only after step 11 succeeds, revalidate the selected runtime (and re-read
    standalone discovery when applicable), then optionally perform a second
    dry-run/execution pair using the fixed process name and `allProcesses: true`.
13. Never persist, display, log, export, or send the bearer token or short-lived
    plan IDs to any other address.

SessionDock implements both compiled HTTP adapters directly. v2 currently keeps
the same close request, response, and single-use plan semantics as v1; its paths
are code-reviewed constants, not catalog strings. The included
`Invoke-HandleScopeClose.ps1` client demonstrates the same discovery, health
validation, policy checks, safe HTTP options, and dry-run-before-execution
contract—including plan-ID binding—for manual use; SessionDock does not invoke
or copy that script.

## Setup by source

### Included with SessionDock (recommended)

1. Install SessionDock 3.0 or later.
2. Open **Integrations > HandleScope integration**.
3. Keep **Included with SessionDock (recommended)** selected.
4. Choose **Automatic**, `v2`, or `v1`.
5. Select **Enable**. SessionDock checks readiness automatically; wait for
   **Ready** or use **Retry** after a bounded failure.

Do not run a HandleScope installer or PowerShell script, approve UAC, or create
an autostart task. SessionDock owns the child and stops it on disable or exit.

### Standalone HandleScope (advanced)

Use a normal, non-administrator terminal. Choose commands from one of
the following locations; do not mix an extracted-bundle path with an installed
path. These commands are not needed for included mode.

#### From an extracted release bundle

Run these commands from the root of the extracted HandleScope release:

```powershell
# Install the per-user API, start it, and explicitly opt SessionDock in.
.\api\HandleScope.Setup.exe install --start-now --enable-sessiondock
```

Add `--enable-autostart` if the API should also start automatically at sign-in.
The install command creates the installed API location documented below.

#### After the per-user API is installed

These absolute commands work only after installation:

```powershell
# Opt SessionDock in.
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe" enable-sessiondock

# Start an installed API that is not already running.
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe" start
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
setting is never replaced without explicit `--force`; with `--force`, it is
replaced by exactly the minimal `enabled` setting shown above.

See [`../INSTALL.md`](../INSTALL.md) for installation, start/stop, and optional
autostart commands.

## Fixed command mapping

The following standalone-client examples illustrate SessionDock's two request
selectors. They are diagnostic/manual equivalents, not commands SessionDock
runs and not part of included-mode setup. Replace `1234` with the PID returned
by the successful Roblox launch:

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

Treat a missing/stale standalone connection file, failed included parent/pipe
state, unavailable launched PID, unexpected health policy, Roblox executable
trust or runtime identity problem, `401`, `403`, `404`, `409`, `429`, timeout,
or malformed response as a denied optional post-launch operation. Do not fall
back to killing Roblox, closing a raw handle, using an administrator helper, or
invoking an unrelated HandleScope copy.

The PID-scoped operation succeeds only when execution returns `200`, reports at
least one closure, includes the launched PID in `closed`, and reports no
failures. The optional all-process sweep is a separate result and must not be
used to claim that the PID-scoped operation succeeded. A HandleScope failure
must not be reported as failure of the Roblox process that already launched.

See [`../../API.md`](../../API.md) for the exact request contract and
[`../THREAT_MODEL.md`](../THREAT_MODEL.md) for residual same-user risks.
