# HandleScope local API v1 and v2

`HandleScope.Api.exe` is a standard-user, headless Windows process. It binds to
an ephemeral IPv4 loopback port and enforces one compiled automation policy:
`roblox-singleton-event-v1`. It is not a general process or handle-management
broker. The desktop application is independent and does not need to be open.

The API refuses to start when it is elevated, running as a Windows service
account, or running in session 0. It never enables `SeDebugPrivilege`. A
per-user, per-session named semaphore provides a single-instance guard for the
same user session.

SessionDock 3.0 includes this 0.3.0 engine inside `SessionDock.exe`. Its
parent-owned child uses the same HTTP/policy contract but receives bootstrap
data through an inherited anonymous pipe and keeps its token/endpoint in
parent-child memory. The standalone executable described below retains the
disk-based discovery contract for direct clients.

## Install and lifecycle

These commands install the standalone API only. SessionDock 3.0 users should
select **Included with SessionDock (recommended)** and do not need this setup,
PowerShell, UAC, scheduled-task, or autostart flow.

Extract the complete official release ZIP. From a normal, non-administrator
terminal in the extracted `api` directory, run the native setup tool:

```powershell
.\HandleScope.Setup.exe install --start-now --enable-autostart --enable-sessiondock
```

After the release ZIP's GitHub attestation and external SHA-256 checksum have
been verified, this checks the exact internal bundle inventory and manifest
hashes, then installs the API for the current user at:

```text
%LOCALAPPDATA%\Programs\HandleScope\Api
```

Autostart and SessionDock integration are disabled by default. Omit either
corresponding option when that opt-in is not wanted. To install and start only:

```powershell
.\HandleScope.Setup.exe install --start-now
```

Use the installed native setup tool for lifecycle operations:

```powershell
$setup = "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe"
& $setup start
& $setup stop
& $setup enable-sessiondock
& $setup uninstall
```

The seven PowerShell files remain for older automation. The five lifecycle
entry points are exact native-command compatibility wrappers; the shared client
code and manual close client remain unchanged. They are not the recommended
setup interface. PowerShell's `Restricted` policy blocks scripts even after
Mark-of-the-Web is removed. The native tool works without changing or bypassing
that policy. Never use
`-ExecutionPolicy Bypass`, disable antivirus or SmartScreen, run setup as
administrator, or move individual files out of the release bundle. See
[`docs/INSTALL.md`](docs/INSTALL.md) for complete verification, policy, update,
and uninstall guidance.

The native grammar is case-sensitive, rejects duplicate or unknown options,
and accepts no arbitrary executable or filesystem path:

```text
verify
install [--start-now] [--enable-autostart] [--enable-sessiondock] [--allow-downgrade]
start
stop
enable-sessiondock [--force]
uninstall [--keep-diagnostics]
```

Exit code `0` means success, `2` means invalid command grammar, `3` means a
security, integrity, identity, or environment refusal, and `4` means a trusted
lifecycle or Windows operation failed. Callers must treat every other result as
failure and must not retry by weakening policy.

## Connection discovery and included bootstrap

While running as a standalone application, the API publishes its active
discovery document at:

```text
%LOCALAPPDATA%\HandleScope\connection.json
```

Example schema (the port, token, PID, and start time vary on every start):

```json
{
  "apiVersion": "v1",
  "baseUrl": "http://127.0.0.1:51327",
  "token": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
  "processId": 1234,
  "startedAtUtc": "2026-07-21T12:00:00+00:00"
}
```

The directory and file ACLs are restricted to the current Windows user. The
token is 256 random bits encoded as 43 base64url characters and changes on
every restart. Treat the file as a live credential: read and validate it for
each operation, and never hard-code, retain, display, log, or transmit its
token. A client must accept only `http://127.0.0.1:<port>` with no user info,
path, query, or fragment, and must disable proxies and redirects.

The discovery schema deliberately remains v1 even when a client later negotiates
v2. The API removes a connection document it owns during normal shutdown.
Clients must still verify the named process and the exact `/v1/health` response;
a stale file is not proof that the API is available.

The SessionDock-included child does not create or read this file. SessionDock
creates an inherited anonymous pipe before launch and transfers only the bounded
bootstrap material to its exact child. The rotating token and ephemeral numeric
IPv4 loopback endpoint remain in parent/child memory and never enter a command
line, environment variable, setting, log, diagnostics, or UI. The child verifies
its parent and exits when the parent lifetime ends.

## Compatibility negotiation

HandleScope 0.2.2 added authenticated `GET /v1/metadata`. This additive endpoint
does not change discovery or the legacy health document. Its exact response is:

```json
{
  "schemaVersion": 1,
  "productVersion": "0.3.0",
  "discoveryApiVersion": "v1",
  "supportedApiVersions": ["v1", "v2"],
  "preferredApiVersion": "v2",
  "policies": ["roblox-singleton-event-v1"],
  "capabilities": [
    "handlescope.http.v1",
    "handlescope.http.v2",
    "handlescope.plan.single-use.v1",
    "handlescope.policy.roblox-singleton-event.v1",
    "handlescope.setup.native.v1"
  ]
}
```

Clients must authenticate this request with the current discovery token, reject
unknown or duplicate fields, and cross-check `productVersion`, API contracts,
policy, and capabilities against a separately authenticated executable/release
identity. Metadata may select only a protocol adapter already compiled into the
client; it cannot define paths, request shapes, or parsers. A legacy 0.1.x
runtime has no metadata endpoint and remains usable through the compiled v1
adapter when its executable identity is separately authorized.

The desktop compatibility selector writes only
`%LOCALAPPDATA%\HandleScope\compatibility.json`. Automatic and v2 prefer v2;
legacy v1 prefers v1. Both endpoint families remain active in every mode, so an
older v1 client is never locked out. The preference takes effect when the API
next starts and does not download, install, or replace software.

## Supported close policy

Every close request must match all of these rules:

- The target is `RobloxPlayerBeta.exe` in the API user's current interactive
  Windows session.
- The target is owned by the same Windows SID and is not elevated.
- The executable resolves without a reparse-point path beneath a supported
  Roblox `Versions\version-*` directory, has Roblox's expected version identity,
  and has a trusted `Roblox Corporation` Authenticode signature.
- The handle name is exactly
  `\Sessions\<current-session>\BaseNamedObjects\ROBLOX_singletonEvent`.
- The object type is exactly `Event`, the access mask is exactly `0x001F0003`,
  and the match mode is `exact`.
- Raw handle values and `closeAll: true` are rejected.
- A name selector must use `allProcesses: true`; a positive PID selector must
  use `allProcesses: false`. At most 32 name-selected processes may pass every
  owner, session, elevation, and executable-trust check; unauthorized
  lookalikes do not consume that cap.

The server accepts only the documented JSON properties with their exact casing,
requires each boolean, rejects duplicate or unknown properties, and limits the
request body to 8 KiB. The request content type must be exactly
`application/json` (optional media-type parameters such as `charset` are
allowed); lookalikes such as `application/jsonp` are rejected.

## Required dry-run sequence

A close is a two-request operation:

1. Send the exact allowed request with `dryRun: true`.
2. Review the successful response and retain its random 43-character base64url
   `planId` only for this operation.
3. Within five seconds, send the identical selector with `dryRun` changed to
   `false` and the returned `planId` added at the top level.

Each successful dry run creates an independent single-use, in-memory plan, even
when another client reviews the same selector. Expiry uses monotonic elapsed
time rather than the Windows wall clock. The execution request consumes its
specific plan even if closure later fails, so the ID cannot be replayed. The
server revalidates the target owner, session, elevation state, executable,
process creation time, handle identity, and access mask before closure.
Concurrent operations are rejected.

The included PowerShell client performs both requests automatically. Use
`-DryRun` to stop after review:

```powershell
$client = "$env:LOCALAPPDATA\Programs\HandleScope\Api\Invoke-HandleScopeClose.ps1"
$sessionId = (Get-Process -Id $PID).SessionId
$eventName = "\Sessions\$sessionId\BaseNamedObjects\ROBLOX_singletonEvent"

& $client `
  -ProcessName 'RobloxPlayerBeta' `
  -HandleName $eventName `
  -Type 'Event' `
  -Access '0x001F0003' `
  -Exact `
  -AllProcesses
```

For one known Roblox process, use `-ProcessId <positive-pid>` and omit
`-ProcessName` and `-AllProcesses`. `-HandleValue` and `-CloseAll` are not
accepted by the release policy.

## Direct HTTP contract

Clients that do not use the included PowerShell client send this exact request
shape, substituting the current positive Windows session number:

```json
{
  "process": { "name": "RobloxPlayerBeta" },
  "handle": {
    "name": "\\Sessions\\1\\BaseNamedObjects\\ROBLOX_singletonEvent",
    "match": "exact",
    "type": "Event",
    "access": "0x001F0003"
  },
  "dryRun": true,
  "closeAll": false,
  "allProcesses": true
}
```

After a successful review, send the same JSON with `dryRun` changed to `false`
and the exact returned plan ID added, for example:

```json
{
  "process": { "name": "RobloxPlayerBeta" },
  "handle": {
    "name": "\\Sessions\\1\\BaseNamedObjects\\ROBLOX_singletonEvent",
    "match": "exact",
    "type": "Event",
    "access": "0x001F0003"
  },
  "dryRun": false,
  "closeAll": false,
  "allProcesses": true,
  "planId": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
}
```

Do not include `planId` in a dry-run request, and never reuse or log it. A direct
client must implement the discovery validation above with an HTTP stack
configured to disable proxies, redirects, cookies, and automatic credential
forwarding and to bound timeouts and response sizes. Do not use a convenience
HTTP command whose proxy or redirect behavior has not been made explicitly
fail-closed.

## Responses

A successful operation reports the policy ID, whether it was a dry run, process
and match counts, redacted match records, closures, failures, and skipped
processes. A successful dry run also reports its `planId`; execution and
unsuccessful reviews do not issue one. Match records replace the full
session-specific path with the literal `ROBLOX_singletonEvent`; raw handle
values, kernel object addresses, and native names are not returned by the API.

Important statuses include:

- `200`: the dry run or execution completed without a reported failure;
- `202`: shutdown was accepted;
- `207`: approved scanning or closing work failed, including an ambiguous
  handle match;
- `400`, `413`, or `415`: the request shape, size, or content type was invalid;
- `401`: the bearer credential was absent or invalid;
- `403`: the browser-origin check or compiled automation policy denied the
  request;
- `404`: no approved process or handle matched;
- `409`: too many authorized targets, or the supplied plan is absent, expired,
  mismatched, or already consumed;
- `429`: another operation is in progress.

Errors use a short machine-readable shape such as `{"error":"policy_denied"}`
and intentionally omit internal exception details.

## Endpoints

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| `GET` | `/v1/health` | None | Readiness, API version, and policy ID |
| `GET` | `/v2/health` | None | v2 readiness plus product and protocol preference metadata |
| `GET` | `/v1/metadata` | Bearer token | Strict compatibility and capability metadata |
| `POST` | `/v1/handles/close` | Bearer token | Dry-run or execute the exact compiled policy |
| `POST` | `/v2/handles/close` | Bearer token | v2 alias of the same exact request/response policy |
| `POST` | `/v1/shutdown` | Bearer token | Stop the headless host |
| `POST` | `/v2/shutdown` | Bearer token | Stop the headless host through v2 |

Authenticated endpoints reject requests that present common browser-origin
headers. That is defense in depth, not a substitute for protecting the bearer
token from other processes running as the same user.

## SessionDock integration

SessionDock 3.0 offers **Included with SessionDock (recommended)** and
**Standalone HandleScope (advanced)**. Included mode compiles the reviewed
HandleScope 0.3.0 source into `SessionDock.exe` and owns the non-elevated child,
pipe bootstrap, loopback endpoint, token, and shutdown. It uses no separate
download, installer, PowerShell, UAC, scheduled task, autostart, updater, or
uninstaller.

Advanced standalone mode retains this document's discovery file, but
SessionDock never downloads, installs, starts, stops, updates, downgrades,
reconfigures, or uninstalls that external application. The standalone API must
already be running.

For that advanced source, SessionDock's separate **Standalone runtime version**
selector offers **Automatic**, **Keep the installed version**, and exact
signed-catalog-reviewed compatible versions. These are authorization
requirements for the already running runtime, not package or lifecycle
commands. A stale exact pin remains visible and can be recovered by choosing
Automatic, Keep the installed version, or another reviewed exact version.

Opening the integration panel remains local-only. The standalone-only
**Refresh reviewed versions** action explicitly fetches and verifies the latest
signed compatibility catalog, preserves the selected runtime source,
standalone version, and API preference, and never downloads, installs, starts,
stops, updates, downgrades, reconfigures, or uninstalls a runtime.

For each launch operation, both sources require policy
`roblox-singleton-event-v1`, negotiate only the compiled/authenticated v1 or v2
adapter selected by Automatic/`v2`/`v1`, construct only the exact
session-specific recipe above, perform a dry run, and consume the matching plan
at most once. SessionDock remains usable when either source is disabled, absent,
or denies the request.

The complete client boundary is in
[`docs/integrations/sessiondock.md`](docs/integrations/sessiondock.md).

No standalone command is required for included mode. For advanced standalone
mode, passing `--enable-sessiondock` to native setup's `install` command is the
simplest explicit opt-in. After installation, a user can also enable
SessionDock's side separately without copying a token or endpoint:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe" enable-sessiondock
```

The helper writes only `%LOCALAPPDATA%\SessionDock\handlescope.json` with
`enabled: true`. When no canonical setting exists, it recognizes and copies the
former `%LOCALAPPDATA%\RobloxOne\handlescope.json` only when that legacy file is
the minimal enabled opt-in. It never deletes legacy data, starts either
application, or lets legacy state overwrite a canonical setting. Replacing an
existing non-minimal canonical setting requires explicit `--force`.
