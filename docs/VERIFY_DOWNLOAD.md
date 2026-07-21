# Verify a HandleScope download

Download the ZIP, matching `.spdx.json`, and `SHA256SUMS.txt` from the same
published [HandleScope GitHub release](https://github.com/Makmatoe/HandleScope/releases).
Do not install files copied from an issue, pull request, chat attachment,
mirror, or workflow-artifact page.

HandleScope is intentionally distributed without Authenticode code signing so
the project does not require a paid certificate or signing service. Windows can
therefore display **Unknown publisher** or a SmartScreen warning. Verify the
release before removing its download marker or running any file.

## 1. Verify GitHub provenance

With the GitHub CLI installed, verify the downloaded ZIP against this public
repository's build attestation:

```powershell
gh attestation verify .\HandleScope-0.1.0-win-x64.zip `
  --repo Makmatoe/HandleScope
```

The command must report a verified attestation issued for
`Makmatoe/HandleScope`. A checksum proves only that two downloaded files agree;
the attestation also binds the ZIP to the repository workflow that produced it.

For an immutable GitHub Release, recent GitHub CLI versions can additionally
verify the published release-asset digest:

```powershell
gh release verify-asset v0.1.0 .\HandleScope-0.1.0-win-x64.zip `
  --repo Makmatoe/HandleScope
```

## 2. Verify the SHA-256 manifest

Compare the ZIP and SBOM hashes with their exact lines in `SHA256SUMS.txt`:

```powershell
$expected = @{}
Get-Content .\SHA256SUMS.txt | ForEach-Object {
  if ($_ -notmatch '^(?<hash>[0-9a-f]{64})  (?<name>[A-Za-z0-9][A-Za-z0-9._-]*)$') {
    throw "Invalid checksum line: $_"
  }
  if ($expected.ContainsKey($Matches.name)) { throw "Duplicate checksum: $($Matches.name)" }
  $expected[$Matches.name] = $Matches.hash
}

foreach ($name in @(
  'HandleScope-0.1.0-win-x64.zip',
  'HandleScope-0.1.0-win-x64.spdx.json')) {
  $actual = (Get-FileHash ".\$name" -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -cne $expected[$name]) { throw "SHA-256 mismatch: $name" }
}
```

Stop if either verification fails or identifies another repository.

## 3. Unblock, extract, and verify the bundle

Only after successful provenance and checksum verification, remove the ZIP's
Windows download marker and extract it to a new local directory:

```powershell
Unblock-File .\HandleScope-0.1.0-win-x64.zip
Expand-Archive .\HandleScope-0.1.0-win-x64.zip -DestinationPath .\HandleScope
```

Do not extract into a symbolic link, junction, cloud placeholder, or existing
application directory. Then validate the complete API inventory and its
internal per-file hashes without installing anything:

```powershell
& .\HandleScope\HandleScope-0.1.0-win-x64\api\Install-HandleScopeApi.ps1 `
  -VerifyOnly
```

The command must report that the inventory and manifest hashes are valid.
`CONTENTS.sha256` covers every bundled file other than the manifest itself, and
the external SPDX document records the same bundle inventory and embedded .NET
runtime components.

Unblocking is not a trust mechanism; it only allows verified, unsigned scripts
to run under common `RemoteSigned` PowerShell policies. Never use
`-ExecutionPolicy Bypass` to work around a failed verification or an
organization's policy.
