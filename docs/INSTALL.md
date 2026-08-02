# Install HandleScope

HandleScope supports Windows 10 and Windows 11 on x64. Official release builds
are self-contained; end users do not need to install the .NET SDK or runtime.
Both the desktop application and local API run as the current standard user and
must be started from a normal, non-administrator session.

> [!NOTE]
> SessionDock 3.0 includes HandleScope engine 0.3.0 inside `SessionDock.exe`.
> SessionDock users should install only SessionDock and select **Included with
> SessionDock (recommended)**. The standalone steps below are for direct
> HandleScope use or **Standalone HandleScope (advanced)** only.

## Verify the release

Download the release ZIP, matching SPDX SBOM, and `SHA256SUMS.txt` from the same
published GitHub release. Do not install copies from issues, pull requests,
chat attachments, mirrors, or workflow-artifact pages.

Before running any file:

1. Verify the ZIP's GitHub artifact attestation and SHA-256 hash against
   `SHA256SUMS.txt`.
2. Only after verification, use `Unblock-File` on the downloaded ZIP so Windows
   does not carry its Internet-zone marker into the verified extracted files.
3. Extract the complete ZIP to a new local directory that is not a symbolic
   link, junction, cloud placeholder, or other reparse-point path.
4. Run `api\HandleScope.Setup.exe verify` for the exact bundle inventory and
   internal file hashes.
5. Review the release notes and provenance shown on the GitHub release.

Exact commands are in [`VERIFY_DOWNLOAD.md`](VERIFY_DOWNLOAD.md). Stop if the
repository identity, provenance, hash, or file inventory does not match.

PowerShell execution policy, Mark-of-the-Web, and antivirus reputation are
different controls. `Restricted` blocks `.ps1` files even when they are
unblocked; `Unblock-File` removes only the Internet-zone marker and is not a
trust check. `HandleScope.Setup.exe` is native, so it does not need or change a
PowerShell policy. Organization-enforced application control and security
software can still refuse any executable.

`-ExecutionPolicy` is an option for starting a new PowerShell process, not a
standalone command or an installer-script parameter. Appending
`-ExecutionPolicy Bypass`, `--ExecutionPolicy`, or `--ExecutionBypass` to a
lifecycle command cannot make a blocked script start and is not a supported
HandleScope setup path.

The release is intentionally not Authenticode-signed because HandleScope uses
no paid certificate or signing service. Windows may show **Unknown publisher**,
a SmartScreen warning, or a security-product reputation block. Verify the
canonical release, but do not disable antivirus, SmartScreen, Smart App Control,
or organization policy. If a managed device still refuses the verified file,
ask its administrator to review the canonical asset and its published hashes.

A browser message such as **Virus scan failed** occurs before HandleScope setup
runs. It means that device's browser-to-security-product download scan did not
complete or refused the file; it is not a PowerShell execution-policy error.
The same asset can therefore download on one PC but fail on another because the
devices have different security products, policy, reputation state, or scanner
health. Check the browser download details and Windows Security protection
history, install current security intelligence, and use the canonical GitHub
release. On a managed device, give the asset name and published SHA-256 to the
administrator. Do not disable scanning or add a broad exclusion.

## Portable desktop

Run the extracted desktop application directly:

```powershell
.\desktop\HandleScope.exe
```

Do not choose **Run as administrator**. The desktop displays only accessible,
non-elevated processes owned by the current Windows user in the current
interactive session. Delete the extracted release directory after closing the
application if you no longer want the portable desktop.

## Per-user local API

If the installer reports a legacy `HandleScope Local API` scheduled task, it
stops without changing that task. Use the matching uninstaller from the same
older build, or inspect and remove the legacy task manually in Windows Task
Scheduler before continuing. Also remove that older build's installation only
after verifying its path. The new standard-user installer intentionally does
not modify an unexpected older task or request UAC approval on its behalf.

From a normal, non-administrator terminal, change to the extracted `api`
directory and run this recommended native setup command:

```powershell
.\HandleScope.Setup.exe install --start-now --enable-autostart --enable-sessiondock
```

Omit `--enable-autostart` if the API should not start automatically at sign-in,
and omit `--enable-sessiondock` if SessionDock should not be opted in. Both
options are explicit; neither is enabled silently.

The setup tool independently requires the fixed eleven-file API inventory,
rejects linked or ambiguous source paths, checks manifest hashes through locked
handles, stages and re-verifies the replacement, and installs it at:

```text
%LOCALAPPDATA%\Programs\HandleScope\Api
```

The operation is per-user and does not request UAC approval. It never launches
PowerShell, changes saved execution policy, overrides Group Policy, or uses
`Bypass` or `Unrestricted`.

Endpoint security may attach alternate data streams such as
`:mshield:$DATA` to downloaded or extracted files. Setup treats these as
untrusted, source-only endpoint metadata rather than product content. It accepts
only well-formed named `$DATA` streams within strict bounds: at most eight per
file, no more than 64 KiB each, and no more than 128 KiB total. A
`Zone.Identifier` stream has its own 4 KiB limit and strict syntax validation.
Only the locked unnamed data stream is hashed and copied. No named stream is
copied from the release, and every staged and installed file must have only its
unnamed data stream. Malformed, excessive, changing, or residual streams fail
closed.

After each staged file matches the reviewed release manifest, the installer
removes only that installed copy's Windows download marker. This does not
change PowerShell execution policy. It prevents a marker inherited from the
ZIP from making the verified installed lifecycle scripts look remote under
`RemoteSigned`.

Autostart is off on a first installation. To install and start without
autostart or SessionDock integration, use:

```powershell
.\HandleScope.Setup.exe install --start-now
```

When `--enable-autostart` is supplied, setup creates one scheduled task
for the current Windows SID and interactive logon. The task uses `RunLevel
Limited`; it does not run as administrator, another user, a service account, or
session 0. Installing an update without the switch does not remove an autostart
task that was already enabled. At completion, the installer reports the task's
observed enabled, disabled, or absent state, including a state preserved during
an update.

The API publishes its rotating local connection credential under
`%LOCALAPPDATA%\HandleScope\connection.json`. Treat that file as secret and do
not copy, log, commit, or upload it. See [`../API.md`](../API.md) for the strict
Roblox singleton policy and client contract.

## Connect SessionDock

[SessionDock](https://github.com/Makmatoe/SessionDock) 3.0 has two source
choices:

- **Included with SessionDock (recommended)** uses the reviewed HandleScope
  0.3.0 engine compiled into `SessionDock.exe`. Install only SessionDock, choose
  Automatic/`v2`/`v1`, then select **Enable**. SessionDock checks readiness
  automatically; wait for **Ready** or use **Retry** after a bounded failure.
  There is no HandleScope download, install command, PowerShell policy, UAC,
  scheduled task, autostart, or separate update.
- **Standalone HandleScope (advanced)** connects to an API you intentionally
  installed and started with this guide. SessionDock never downloads, installs,
  starts, stops, updates, downgrades, reconfigures, or uninstalls it.

The signed compatibility catalog remains for older SessionDock clients and
reviewed advanced-standalone identities. SessionDock 3.0's included flow does
not download or execute from it. SessionDock 2.9.x retains the older separate
installation flow; SessionDock 2.8.x remains on its HandleScope 0.2.2 path.
The exact current contract is in
[`integrations/sessiondock.md`](integrations/sessiondock.md).

For advanced standalone mode, `--enable-sessiondock` on the install command is
the easiest explicit opt-in. To enable it separately later, run:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe" enable-sessiondock
```

The helper writes only this non-secret file:

```text
%LOCALAPPDATA%\SessionDock\handlescope.json
```

Its complete content is `{"enabled":true}` (formatting aside). It does not
start HandleScope, start SessionDock, copy the connection token, or inspect or
modify Roblox accounts. If the canonical file is absent, an older minimal
opt-in at `%LOCALAPPDATA%\RobloxOne\handlescope.json` is copied to the canonical
path without deleting legacy data. Legacy state never overwrites a canonical
file. If the canonical file has the integration disabled or uses a non-minimal
format, setup requires the explicit `--force` option before replacing it.

Select **Standalone HandleScope (advanced)** in SessionDock, then start the API
separately before enabling/retrying the source or launching:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe" start
```

## Start and stop

Use the installed native setup tool from a normal terminal:

```powershell
$setup = "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe"
& $setup start
& $setup stop
```

Start validates any active discovery document and refuses to mask a live or
uninspectable API process behind an unhealthy connection. It preserves
discovery data when startup safety is uncertain; a definitively stale document
is replaced only when the new API publishes its connection. Blocked startup
prints wait, authenticated-stop, and exact-path Task Manager recovery guidance.
Stop authenticates to the current loopback instance and waits for it to exit;
it does not terminate unrelated processes by name.

## Update

HandleScope does not download or install updates automatically. To update:

1. Download and verify the complete newer release as described above.
2. Close the portable desktop if it is running.
3. Run the newer release's `api\HandleScope.Setup.exe install --start-now` from
   a normal terminal. Add `--enable-autostart` if enabling or refreshing
   the optional task is intended.

The installer stops the currently installed API before replacing its files and
refuses a version downgrade by default. `--allow-downgrade` is an explicit
recovery control; use it only after verifying the older official release and
understanding why rollback is necessary.

## Uninstall

Run the installed native setup tool from a normal terminal:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\HandleScope.Setup.exe" uninstall
```

It authenticates and stops the API, validates and removes the expected
per-user autostart task if present, removes the API installation, and deletes
`%LOCALAPPDATA%\HandleScope`. Add `--keep-diagnostics` only when you intentionally
want to retain the local runtime directory and log for troubleshooting.

## Legacy PowerShell compatibility

The seven `.ps1` files remain in the release and installed directory so older
automation does not break. In 0.3.0 the install, start, stop, SessionDock opt-in,
and uninstall scripts are compatibility wrappers around fixed native commands.
They are not the recommended interface and remain subject to PowerShell policy.
If a verified older 0.1.x or 0.2.x release must be recovered manually, a new
Windows PowerShell child may use process-scoped `RemoteSigned` after the ZIP is
verified and unblocked. That setting cannot override `MachinePolicy` or
`UserPolicy`. Never substitute `Bypass`, persist a policy change, or disable a
security product to make a legacy script run.

## Build from source

Source-development requirements are PowerShell and the .NET SDK selected by
[`../global.json`](../global.json). From the repository root:

```powershell
.\scripts\Build.ps1 -CI
```

This uses locked restore, builds with warnings as errors, and runs the
controlled child-process integration harness. Locally built files are not
GitHub-attested release assets. The packaged installer accepts only a complete
finalized bundle with its matching internal manifest. Do not regenerate a
manifest to make an unreviewed or partial bundle look official.

SessionDock synchronizes its included component from an immutable HandleScope
tag into `SessionDock.HandleScope/Upstream/` and records the repository,
version, tag, commit, allowlisted files, and hashes in
`SessionDock.HandleScope/handlescope-upstream.json`. Fixes to shared Core/API behavior must
land here first and be synchronized into SessionDock; do not maintain divergent
copies. SessionDock must keep the included child non-elevated, parent-owned, and
loopback-only with token bootstrap through an inherited pipe. It must never
modify a separately installed standalone HandleScope. See the
[`SessionDock integration contract`](integrations/sessiondock.md).
