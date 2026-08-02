# Privacy

HandleScope is local Windows software. It contains no telemetry, analytics,
advertising, crash upload, account, cloud-sync, or remote-update service. It
does not send process information, handle names, file paths, API usage, or
diagnostics to the maintainer. The headless API listens only on IPv4 loopback
for clients on the same machine.

## Data HandleScope can access

When requested by the user, the desktop can inspect running-process metadata
and named handles for non-elevated processes owned by the same Windows user in
the same interactive session. Resolved handle names can contain personal or
sensitive information, including usernames, local file paths, Windows session
numbers, and application-specific object names. The desktop displays that
information locally and does not upload or automatically persist it. Choosing
**Copy recurring command** places the supported Roblox command on the Windows
clipboard at the user's request.

The local API can inspect only candidate Roblox processes needed for its
compiled singleton-event policy. Its HTTP responses redact raw handle values,
kernel object addresses, native object names, and the session-specific event
path; the returned name is the literal `ROBLOX_singletonEvent`. Process IDs,
counts, access/type data, and short error codes can be returned to the
authenticated local client. A successful dry run also returns a random,
single-use plan ID. Plans remain only in API memory for at most five seconds;
their IDs and request bodies are not written to the lifecycle log.

## Files and local persistence

Installing the standalone API places its API and native setup executables,
legacy-compatible lifecycle scripts, runtime manifest, and local documentation
under:

```text
%LOCALAPPDATA%\Programs\HandleScope\Api
```

The API writes only these runtime files under
`%LOCALAPPDATA%\HandleScope`:

- `connection.json`, containing the current loopback URL, API version, process
  ID, start time, and rotating bearer token;
- `compatibility.json`, containing only the user's preferred SessionDock API
  contract mode (`automatic`, `v2`, or legacy `v1`);
- `api.log`, containing minimal lifecycle messages and generic error type/code
  information, not request bodies, target names, or bearer tokens.

The runtime directory and files use an ACL restricted to the current Windows
user and reject a reparse-point runtime directory. The connection document is a
live credential. Do not share, upload, screenshot, or attach it to an issue. The
API removes a connection document it owns during normal shutdown. The log is
bounded to approximately 256 KiB and is reset when that limit is reached.

If the user enables autostart, installation also creates one limited-privilege
scheduled task in a per-user SID path. The task contains the current Windows
account identity and the local API executable path. Autostart is optional and
off by default.

Running `HandleScope.Setup.exe enable-sessiondock` is also optional. It writes
only `%LOCALAPPDATA%\SessionDock\handlescope.json` with an `enabled` boolean. When
that canonical file is absent, the helper may read the former
`%LOCALAPPDATA%\RobloxOne\handlescope.json` to recognize and copy the old
minimal opt-in. It does not delete or modify legacy data, let legacy state
overwrite a canonical setting, read or modify SessionDock accounts, history,
favorites, or Roblox cookies, access the HandleScope bearer token, or launch
either application.

Official builds are compressed, self-contained .NET single-file applications.
On launch, .NET may extract bundled native runtime components beneath the
current user's `%TEMP%\.net` directory. Those runtime files are not telemetry
and do not contain HandleScope's connection token or inspected handle data.
They may remain until normal temporary-file cleanup; stop HandleScope before
removing a matching temporary extraction directory.

Running `HandleScope.Setup.exe uninstall` removes the per-user API installation,
optional autostart task, connection document, and diagnostic log. With
`uninstall --keep-diagnostics`, the runtime directory is retained for local
troubleshooting. The PowerShell lifecycle files remain only as legacy
compatibility wrappers. The portable desktop is removed by deleting its
extracted release directory after the application is closed.

SessionDock 3.0 included mode creates none of those installed files, runtime
documents, or scheduled tasks. HandleScope 0.3.0 code is part of
`SessionDock.exe`. SessionDock starts a parent-owned child, transfers bootstrap
data through an inherited anonymous pipe, and keeps the rotating token and
endpoint only in parent/child memory. They do not enter a connection file,
command line, environment variable, setting, log, diagnostics/export, or UI.
The child exits when disabled or when SessionDock exits. Removing SessionDock
removes the included code but never deletes a separate standalone installation.

## Network behavior

Released HandleScope binaries do not check for updates or contact GitHub,
Roblox, Microsoft, the maintainer, or any other internet service. API traffic
is limited to `127.0.0.1`, and the API's Roblox executable trust check uses
Windows' cache-only verification mode. Downloading a release, visiting GitHub,
building from source, installing/updating SessionDock, an older SessionDock
client's compatibility-catalog check, and artifact-attestation or
release-integrity checks performed by GitHub CLI are separate actions that may
contact their respective services. SessionDock 3.0's included flow makes no
HandleScope download and sends no runtime credentials or process data to the
internet. Selecting **Standalone HandleScope (advanced)** also performs no
download or lifecycle action; any standalone download is initiated and managed
by the user outside SessionDock.

## Sharing diagnostics and security reports

Before sharing a screenshot or diagnostic excerpt, remove usernames, process
identifiers, handle names, local paths, session identifiers, access tokens, and
unrelated application information. Never post a bearer token or security
report in a public issue. Follow [SECURITY.md](SECURITY.md) for private
vulnerability reporting.
