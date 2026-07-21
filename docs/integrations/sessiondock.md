# SessionDock integration

[SessionDock](https://github.com/Makmatoe/RobloxOne) is an optional,
standard-user client of HandleScope local API v1.
The applications remain separate repositories, downloads, installs, processes,
and release channels. HandleScope is not bundled with SessionDock.

The supported integration performs one narrow operation before a Roblox launch:
close the exact session-specific `ROBLOX_singletonEvent` in one or more verified
`RobloxPlayerBeta.exe` processes. HandleScope is not a general process-control
extension point.

## User-control boundary

SessionDock may call HandleScope only after the user explicitly enables the
integration and an already-running API has published:

```text
%LOCALAPPDATA%\HandleScope\connection.json
```

SessionDock must not bundle, download, install, update, uninstall, elevate, or
silently start HandleScope. Users install HandleScope separately and may choose
its optional limited per-user autostart task. SessionDock must remain usable
when HandleScope is absent, stopped, incompatible, busy, or denies a request,
and it must surface a clear local result instead of silently broadening or
retrying the action.

## Required client behavior

For every operation, SessionDock must:

1. Read the connection document again; never cache the bearer token or port.
2. Reject a reparse-point connection file and accept only API version `v1`.
3. Accept only an absolute URL of the form `http://127.0.0.1:<port>` with no user
   info, non-root path, query, or fragment.
4. Confirm that the positive PID names a live `HandleScope.Api` process.
5. Disable proxies, redirects, and cookies, and apply short timeouts and bounded
   response sizes.
6. Call `/v1/health` and require policy `roblox-singleton-event-v1` before
   sending the bearer token.
7. Construct only the exact policy request for the current Windows session.
8. Complete a successful dry run, then make the identical single-use execution
   request within five seconds.
9. Never persist, display, log, export, or send the token to any other address.

The installed `Invoke-HandleScopeClose.ps1` client already implements discovery,
health validation, safe HTTP options, policy argument checks, and the required
two-step request. Calling the verified installed client is preferable to
duplicating the HTTP implementation.

## Explicit local setup

HandleScope includes an opt-in helper after installation:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Enable-SessionDockIntegration.ps1"
```

It writes only `%LOCALAPPDATA%\RobloxOne\handlescope.json` with this logical
content:

```json
{
  "enabled": true
}
```

It does not start either application, access SessionDock account data, or copy
the API token or port. It performs an atomic, reparse-safe write. An existing
setting is never replaced without explicit `-Force`; with `-Force`, it is
replaced by exactly the minimal `enabled` setting shown above.

## Fixed command mapping

A command copied from the HandleScope desktop has this form:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Invoke-HandleScopeClose.ps1" `
  -ProcessName 'RobloxPlayerBeta' `
  -HandleName '\Sessions\<session>\BaseNamedObjects\ROBLOX_singletonEvent' `
  -Type 'Event' `
  -Access '0x001F0003' `
  -Exact `
  -AllProcesses
```

If SessionDock stores the recipe as structured configuration, every field must
remain fixed to the compiled policy:

| HandleScope argument | SessionDock value |
| --- | --- |
| `-ProcessName` | `RobloxPlayerBeta` |
| `-HandleName` | Current session's exact `ROBLOX_singletonEvent` path |
| `-Type` | `Event` |
| `-Access` | `0x001F0003` |
| `-Exact` | `true` |
| `-AllProcesses` | `true` for name-based batch handling |

Do not store a PID, raw handle value, bearer token, API port, or hard-coded
Windows session number. PIDs and handles are reusable, and the event path must
be derived from the active interactive session at operation time. Do not expose
`closeAll`, partial matching, arbitrary process names, object types, access
masks, or paths as integration settings; the server rejects them.

## Failure behavior

Treat a missing or stale connection file, unexpected health policy, Roblox
executable trust or HandleScope install problem, `401`, `403`, `404`, `409`, `429`, timeout, or malformed
response as a denied integration operation. Do not fall back to killing Roblox,
closing a raw handle, using an administrator helper, or invoking an unrelated
HandleScope copy.

Whether SessionDock cancels the associated Roblox launch or lets the user
continue without the HandleScope action is a SessionDock product decision that
must be explicit in its UI. It must never report that HandleScope succeeded
unless the execution response is `200`, reports at least one closure, and
reports no failures.

See [`../../API.md`](../../API.md) for the exact request contract and
[`../THREAT_MODEL.md`](../THREAT_MODEL.md) for residual same-user risks.
