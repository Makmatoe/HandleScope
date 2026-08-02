# Releasing HandleScope

HandleScope releases are generated only from a stable `vMAJOR.MINOR.PATCH`
annotated tag whose commit is also the protected `main` branch tip. A normal
branch push and a manually dispatched workflow cannot publish a release.

The selected no-cost trust model is intentionally transparent: HandleScope is
not Authenticode-signed. The release workflow instead produces an exact
portable ZIP, a SHA-256 manifest, an SPDX 2.3 SBOM, an immutable compatibility
manifest, and GitHub artifact attestations. It uses no certificate, external
signing account, paid service, long-lived credential, or repository secret.
Users must verify downloads and may see **Unknown publisher**, SmartScreen, or
security-product reputation warnings on Windows. Release material must never
recommend disabling those controls.

## One-time repository configuration

Apply [`REPOSITORY_SETTINGS.md`](REPOSITORY_SETTINGS.md), including:

- protected `main` and immutable `v*` tags;
- read-only default workflow permissions;
- an environment named `release`, restricted to protected `v*` tags, with
  `Makmatoe` as the required reviewer and administrator bypass disabled;
- secret scanning, dependency review, CodeQL, private vulnerability reporting,
  and immutable releases.

The repository has one maintainer, so the environment must allow that
maintainer to approve the tag-triggered deployment. It still creates an
explicit confirmation point without inventing an unavailable independent
reviewer. The environment requires no variables and no secrets.

## Prepare a release

1. Review every change affecting execution level, the standard-user runtime
   guard, API policy, process/session identity, handle matching, installation,
   optional autostart, SessionDock integration, native code, or release files.
2. Update the single `<Version>` in `Directory.Build.props`.
3. Add `ReleaseNotes/<version>.md` and move relevant `CHANGELOG.md` entries out
   of **Unreleased**.
4. Run the full local gate:

   ```powershell
   .\scripts\Build.ps1 -CI
   .\scripts\Verify-Release.ps1 -Tag v<version> -RequireCleanWorkingTree
   .\scripts\Publish-Release.ps1 -Version <version>
   .\scripts\Finalize-Release.ps1 `
     -InputDirectory artifacts\release-input `
     -OutputDirectory artifacts\release-output
   .\scripts\Verify-ReleaseAssets.ps1 `
     -Directory artifacts\release-output `
     -Version <version>
   ```

5. Review the exact staged inventory and test the desktop, API, and every fixed
   native setup command on a disposable standard-user Windows x64 machine. Test
   with the normal PowerShell default resolving to `Restricted`, with and without
   Mark-of-the-Web, and without antivirus exclusions. Exercise a bounded
   scanner-style named metadata stream plus malformed, oversized, excessive,
   and residual-stream cases; confirm only source metadata is accepted and no
   named stream reaches staging or installation. Destructive tests may target
   only test-owned processes and handles.
6. Merge through protected `main`. From a clean, synchronized `main`, create
   and push one annotated tag:

   ```powershell
   git tag -a v<version> -m "HandleScope <version>"
   git push origin v<version>
   ```

7. Review and approve the pending `release` environment deployment. Do not
   approve if the tag, commit, workflow, or generated inventory is unexpected.

### SessionDock bundled-source and standalone contracts

This repository is the source of truth for the HandleScope engine synchronized
into SessionDock. For any shared Core/API change:

1. Land and validate it here first.
2. Publish or identify an immutable HandleScope tag and exact protected-main
   commit.
3. In the SessionDock repository, run
   `.\scripts\Sync-BundledHandleScope.ps1` to verify the existing snapshot, or
   `.\scripts\Sync-BundledHandleScope.ps1 -UpstreamPath C:\path\to\HandleScope -Sync`
   to copy the pinned tag/commit from this local checkout. The script performs
   no network operation. Review every synchronized file and the regenerated
   `SessionDock.HandleScope/handlescope-upstream.json`.
4. Confirm the provenance repository, version, tag, commit, allowlisted paths,
   and hashes match this immutable source.
5. Update both repositories' current integration, security, privacy, threat
   model, contributing, and release documents; update SessionDock's displayed
   component version, MIT license/notices, SBOM inputs, localized UI, and tests.
6. Verify SessionDock publishes HandleScope only inside `SessionDock.exe`, with
   no HandleScope executable, installer, script, or component-directory sidecar.

SessionDock's included child must remain non-elevated, current-user/session,
parent-owned, numeric-IPv4-loopback-only, inherited-pipe-bootstrapped, and tied
to the parent's lifetime. The token/endpoint stays in memory. Normal SessionDock
use must require no HandleScope download, standalone install, PowerShell, UAC,
scheduled task, autostart, update, or uninstall action.

The independent standalone release remains fully supported for direct clients
and **Standalone HandleScope (advanced)**. SessionDock must not download,
install, start, stop, update, downgrade, reconfigure, or uninstall that copy.
The immutable compatibility manifest and signed SessionDock catalog remain for
older SessionDock clients and reviewed advanced-standalone identities; they are
authorization data only and cannot define executable behavior. Never revise the
contract of an existing immutable release retroactively or restore SessionDock's
removed in-app downloader/installer.

SessionDock's standalone runtime-version selector must continue to expose
Automatic, Keep installed, and exact signed-catalog-reviewed compatible
versions independently from its API selector. A stale exact pin must remain
visible and recoverable through Automatic, Keep installed, or another reviewed
exact version. Opening the integration panel remains local-only. The
standalone-only **Refresh reviewed versions** action may fetch only the canonical
signed catalog, must enforce signature, product/repository/key identity,
validity, compatibility, and rollback protection, and must preserve the selected
runtime source, standalone version, and API preference. It must never download,
install, start, stop, update, downgrade, reconfigure, or uninstall a runtime.

No cryptographic tag key is required. Repository and tag rules determine who
may create release tags, while the GitHub attestation binds each asset to the
tag-triggered workflow and source repository.

## Automated release flow

The tag-triggered workflow:

1. verifies the repository, annotated tag, project version, release notes, and
   exact protected-main tip;
2. builds and runs the native setup safety regressions and controlled integration
   harness with the pinned .NET SDK, locked restore, and NuGet.org as the only
   package source;
3. publishes the compressed desktop and API plus an explicitly uncompressed
   native setup executable as self-contained Windows x64 single files without
   debug symbols, and records their embedded .NET runtime components;
4. enforces an exact release inventory, creates `CONTENTS.sha256`, the ZIP,
   external SHA-256 manifest, SPDX SBOM, and immutable compatibility manifest,
   then verifies them independently;
5. transfers only those four verified assets to the protected `release` job;
6. creates a new draft release, refusing to reuse any existing draft or release
   for the tag;
7. uploads and redownloads the draft assets, compares them byte for byte, and
   verifies the complete archive again;
8. creates GitHub artifact attestations and publishes the verified draft.

Only the final protected job receives `contents: write`, `id-token: write`,
`attestations: write`, and `artifact-metadata: write`. All third-party Actions
are pinned to reviewed commit SHAs.

Any unexpected repository, tag mismatch, build or test failure, changed file
inventory, checksum/SBOM mismatch, pre-existing release state, or attestation
failure stops publication. If a failed run leaves a draft, inspect it and
delete that draft before intentionally rerunning the same tag workflow. The
workflow never clobbers assets.

## Verification and rollback

Users should download the ZIP, SBOM, compatibility manifest, and
`SHA256SUMS.txt` from the same release and follow
[`VERIFY_DOWNLOAD.md`](VERIFY_DOWNLOAD.md) before execution.

If a release is suspected of compromise, make it unavailable without reusing,
moving, or deleting its tag, publish a private security advisory, and issue a
higher fixed version. Never replace an asset under an existing version: hashes,
provenance, and immutable-release identity belong to that original tag.
