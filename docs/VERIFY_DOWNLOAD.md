# Verify a HandleScope download

Download the ZIP, matching `.spdx.json`, matching `.release.json`, and
`SHA256SUMS.txt` from the same published
[HandleScope GitHub release](https://github.com/Makmatoe/HandleScope/releases).
Do not install files copied from an issue, pull request, chat attachment,
mirror, or workflow-artifact page.

Use a new, empty directory dedicated to one HandleScope release. Other
projects may also publish a file named `SHA256SUMS.txt`; never reuse a checksum
file already present in a general Downloads directory.

HandleScope is intentionally distributed without Authenticode code signing so
the project does not require a paid certificate or signing service. Windows can
therefore display **Unknown publisher** or a SmartScreen warning. Verify the
release before removing its download marker or running any file.

If the browser reports **Virus scan failed**, no HandleScope code has run. That
message comes from the downloading device's browser/security-product scan and
is separate from PowerShell execution policy. Review the browser's download
details and Windows Security protection history, update the security product,
and use only the canonical GitHub release. A managed-device administrator can
review the exact asset name and published SHA-256. Do not disable protection or
create a broad antivirus exclusion.

## 1. Verify GitHub provenance

With the GitHub CLI installed, first derive the release identity from the
single ZIP in the empty download directory. The filename must match the exact
HandleScope release format:

```powershell
$zipCandidates = @(Get-ChildItem -File -Filter 'HandleScope-*-win-x64.zip')
if ($zipCandidates.Count -ne 1) {
  throw 'Expected exactly one HandleScope win-x64 ZIP in this directory.'
}
$zip = $zipCandidates[0]
if ($zip.Name -cnotmatch '^HandleScope-(?<version>\d+\.\d+\.\d+)-win-x64\.zip$') {
  throw "Unexpected HandleScope ZIP name: $($zip.Name)"
}
$version = $Matches.version
$tag = "v$version"
$assetBaseName = "HandleScope-$version-win-x64"
$sbomName = "$assetBaseName.spdx.json"
$releaseManifestName = "$assetBaseName.release.json"
if (-not (Test-Path -LiteralPath $sbomName -PathType Leaf) -or
    -not (Test-Path -LiteralPath $releaseManifestName -PathType Leaf) -or
    -not (Test-Path -LiteralPath 'SHA256SUMS.txt' -PathType Leaf)) {
  throw 'The matching SBOM, release manifest, or SHA256SUMS.txt is missing.'
}
```

Keep the same PowerShell session open for all following commands. Verify the
downloaded ZIP against this public repository's build attestation:

```powershell
gh attestation verify $zip.FullName `
  --repo Makmatoe/HandleScope
```

The command must report a verified attestation issued for
`Makmatoe/HandleScope`. A checksum proves only that two downloaded files agree;
the attestation also binds the ZIP to the repository workflow that produced it.

For an immutable GitHub Release, recent GitHub CLI versions can additionally
verify the published release-asset digest:

```powershell
gh release verify-asset $tag $zip.FullName `
  --repo Makmatoe/HandleScope
```

## 2. Verify the SHA-256 manifest

Compare the ZIP, SBOM, and release-manifest hashes with their exact lines in
`SHA256SUMS.txt`:

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
  $zip.Name,
  $sbomName,
  $releaseManifestName)) {
  if (-not $expected.ContainsKey($name)) { throw "Missing checksum entry: $name" }
  $actual = (Get-FileHash -LiteralPath $name -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -cne $expected[$name]) { throw "SHA-256 mismatch: $name" }
}
```

Stop if either verification fails or identifies another repository.

## 3. Unblock, extract, and verify the bundle

Only after successful provenance and checksum verification, remove the ZIP's
Windows download marker and extract it to a new local directory:

```powershell
Unblock-File -LiteralPath $zip.FullName
Expand-Archive -LiteralPath $zip.FullName -DestinationPath .\HandleScope
```

Do not extract into a symbolic link, junction, cloud placeholder, or existing
application directory. Then use the release's native setup verifier to validate
the complete API inventory and its internal per-file hashes without installing
anything:

```powershell
$setup = Join-Path .\HandleScope "$assetBaseName\api\HandleScope.Setup.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
  throw 'The native HandleScope setup verifier is missing.'
}
& $setup verify
if ($LASTEXITCODE -ne 0) {
  throw "HandleScope setup verification failed with exit code $LASTEXITCODE."
}
```

The command must report that the inventory and manifest hashes are valid.
`CONTENTS.sha256` covers every bundled file other than the manifest itself, and
the external SPDX document records the same bundle inventory and embedded .NET
runtime components.

An endpoint-security product may add a small named metadata stream while it
scans an extracted file. Native verification bounds and treats that source-only
metadata as untrusted, hashes only the unnamed file data recorded by
`CONTENTS.sha256`, and never copies a named stream into the installation.
Malformed or excessive metadata still causes verification to fail.

Unblocking is not a trust mechanism; it removes only the Windows Internet-zone
marker after provenance and hashes are already trusted. The native verifier
does not launch PowerShell and works when the local PowerShell default is
`Restricted`. It cannot override antivirus, Smart App Control, WDAC, AppLocker,
or other organization policy. Never use `-ExecutionPolicy Bypass`, disable
protection, or weaken policy to work around a failed verification or refusal.
