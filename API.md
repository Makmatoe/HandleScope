# HandleScope local API v1

`HandleScope.Api.exe` is a standard-user, headless Windows process. It binds to
an ephemeral IPv4 loopback port and enforces one compiled automation policy:
`roblox-singleton-event-v1`. It is not a general process or handle-management
broker. The desktop application is independent and does not need to be open.

The API refuses to start when it is elevated, running as a Windows service
account, or running in session 0. It never enables `SeDebugPrivilege`. A
per-user, per-session named semaphore provides a single-instance guard for the
same user session.

## Install and lifecycle

Extract the complete official release ZIP. From a normal, non-administrator
PowerShell window in the extracted `api` directory, run:

```powershell
.\Install-HandleScopeApi.ps1 -StartNow -EnableAutostart -EnableSessionDock
```

After the release ZIP's GitHub attestation and external SHA-256 checksum have
been verified, this checks the exact internal bundle inventory and manifest
hashes, then installs the API for the current user at:

```text
%LOCALAPPDATA%\Programs\HandleScope\Api
```

Autostart and SessionDock integration are disabled by default. Omit either
corresponding switch when that opt-in is not wanted. To install and start only:

```powershell
.\Install-HandleScopeApi.ps1 -StartNow
```

The installed lifecycle scripts are:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Start-HandleScopeApi.ps1"
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Stop-HandleScopeApi.ps1"
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Enable-SessionDockIntegration.ps1"
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Uninstall-HandleScopeApi.ps1"
```

Do not use `-ExecutionPolicy Bypass`, run the scripts as administrator, or move
individual files out of the release bundle. Verify and unblock the downloaded
ZIP before extraction, or follow your organization's PowerShell policy. See
[`docs/INSTALL.md`](docs/INSTALL.md) for update and uninstall guidance.
After verifying each installed file against the release manifest, the installer
removes its inherited Windows download marker so installed scripts work under
normal `RemoteSigned` policy without weakening that policy.

## Connection discovery

While running, the API publishes its active discovery document at:

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

The API removes a connection document it owns during normal shutdown. Clients
must still verify the named process and `/v1/health`; a stale file is not proof
that the API is available.

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
| `POST` | `/v1/handles/close` | Bearer token | Dry-run or execute the exact compiled policy |
| `POST` | `/v1/shutdown` | Bearer token | Stop the headless host |

Authenticated endpoints reject requests that present common browser-origin
headers. That is defense in depth, not a substitute for protecting the bearer
token from other processes running as the same user.

## SessionDock integration

SessionDock must treat HandleScope as a separately released, optional local
dependency. A compatible SessionDock release may offer the strictly confirmed,
version-pinned managed setup defined in the complete client boundary below; it
must not bundle HandleScope or silently install, update, start, or configure it.
For each launch operation it must discover the current connection, require
health policy `roblox-singleton-event-v1`, construct only the exact
session-specific recipe above, perform a dry run, and use the resulting plan at
most once. It must remain usable when HandleScope is absent or denies the
request.

The complete client boundary is in
[`docs/integrations/sessiondock.md`](docs/integrations/sessiondock.md).

Passing `-EnableSessionDock` to the installer is the simplest explicit opt-in.
After installation, a user can also enable SessionDock's side of that boundary
separately without copying a token or endpoint:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Enable-SessionDockIntegration.ps1"
```

The helper writes only `%LOCALAPPDATA%\SessionDock\handlescope.json` with
`enabled: true`. When no canonical setting exists, it recognizes and copies the
former `%LOCALAPPDATA%\RobloxOne\handlescope.json` only when that legacy file is
the minimal enabled opt-in. It never deletes legacy data, starts either
application, or lets legacy state overwrite a canonical setting. Replacing an
existing non-minimal canonical setting requires explicit `-Force`.
