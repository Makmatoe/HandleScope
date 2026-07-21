# Install HandleScope

HandleScope supports Windows 10 and Windows 11 on x64. Official release builds
are self-contained; end users do not need to install the .NET SDK or runtime.
Both the desktop application and local API run as the current standard user and
must be started from a normal, non-administrator session.

## Verify the release

Download the release ZIP, matching SPDX SBOM, and `SHA256SUMS.txt` from the same
published GitHub release. Do not install copies from issues, pull requests,
chat attachments, mirrors, or workflow-artifact pages.

Before running any file:

1. Verify the ZIP's GitHub artifact attestation and SHA-256 hash against
   `SHA256SUMS.txt`.
2. Only after verification, use `Unblock-File` on the downloaded ZIP so its
   unsigned scripts can run under common `RemoteSigned` PowerShell policies.
3. Extract the complete ZIP to a new local directory that is not a symbolic
   link, junction, cloud placeholder, or other reparse-point path.
4. Run the installer's `-VerifyOnly` check for the exact bundle inventory and
   internal file hashes.
5. Review the release notes and provenance shown on the GitHub release.

Exact commands are in [`VERIFY_DOWNLOAD.md`](VERIFY_DOWNLOAD.md). Stop if the
repository identity, provenance, hash, or file inventory does not match.

The release is intentionally not Authenticode-signed because HandleScope uses
no paid certificate or signing service. Windows may show **Unknown publisher**
or a SmartScreen warning. That warning is expected for this delivery model, but
it is not a substitute for the verification steps above.

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

From a normal PowerShell window, change to the extracted `api` directory and
run:

```powershell
.\Install-HandleScopeApi.ps1 -StartNow
```

The installer independently requires the fixed nine-file API inventory,
rejects linked source paths, checks manifest hashes, stages and re-verifies the
replacement, and installs it at:

```text
%LOCALAPPDATA%\Programs\HandleScope\Api
```

The operation is per-user and does not request UAC approval. Do not use
`-ExecutionPolicy Bypass`; verify and unblock the ZIP before extraction, or
follow the policy set by your organization or administrator.

Autostart is off on a first installation. To opt into it, add
`-EnableAutostart`:

```powershell
.\Install-HandleScopeApi.ps1 -StartNow -EnableAutostart
```

This creates one scheduled task for the current Windows SID and interactive
logon. The task uses `RunLevel Limited`; it does not run as administrator,
another user, a service account, or session 0. Installing an update without the
switch does not remove an autostart task that was already enabled.

The API publishes its rotating local connection credential under
`%LOCALAPPDATA%\HandleScope\connection.json`. Treat that file as secret and do
not copy, log, commit, or upload it. See [`../API.md`](../API.md) for the strict
Roblox singleton policy and client contract.

## Connect SessionDock

[SessionDock](https://github.com/Makmatoe/RobloxOne) remains a separate
download and never installs or starts HandleScope itself. After the API is
installed, explicitly enable the local v1 integration with:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Enable-SessionDockIntegration.ps1"
```

The helper writes only this non-secret file:

```text
%LOCALAPPDATA%\RobloxOne\handlescope.json
```

Its complete content is `{"enabled":true}` (formatting aside). It does not
start HandleScope, start SessionDock, copy the connection token, or inspect or
modify Roblox accounts. If an existing file has the integration disabled or
uses an older format, the helper requires the explicit `-Force` switch before
replacing that file with the minimal setting.

Start the API separately before launching through SessionDock:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Start-HandleScopeApi.ps1"
```

## Start and stop

Use the installed scripts from a normal PowerShell window:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Start-HandleScopeApi.ps1"
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Stop-HandleScopeApi.ps1"
```

Start validates any active discovery document and refuses to mask an unknown
or unhealthy API process. Stop authenticates to the current loopback instance
and waits for it to exit; it does not terminate unrelated processes by name.

## Update

HandleScope does not download or install updates automatically. To update:

1. Download and verify the complete newer release as described above.
2. Close the portable desktop if it is running.
3. Run the newer release's `api\Install-HandleScopeApi.ps1 -StartNow` from a
   normal PowerShell window. Add `-EnableAutostart` if enabling or refreshing
   the optional task is intended.

The installer stops the currently installed API before replacing its files and
refuses a version downgrade by default. `-AllowDowngrade` is an explicit
recovery control; use it only after verifying the older official release and
understanding why rollback is necessary.

## Uninstall

Run the installed uninstaller from a normal PowerShell window:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Uninstall-HandleScopeApi.ps1"
```

It authenticates and stops the API, validates and removes the expected
per-user autostart task if present, removes the API installation, and deletes
`%LOCALAPPDATA%\HandleScope`. Add `-KeepDiagnostics` only when you intentionally
want to retain the local runtime directory and log for troubleshooting.

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

SessionDock remains a separate optional client. It must not bundle, download,
install, elevate, silently start, update, or uninstall HandleScope. See
[`integrations/sessiondock.md`](integrations/sessiondock.md).
