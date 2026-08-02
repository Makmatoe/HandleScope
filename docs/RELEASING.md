# Releasing HandleScope

HandleScope releases are generated only from a stable `vMAJOR.MINOR.PATCH`
annotated tag whose commit is also the protected `main` branch tip. A normal
branch push and a manually dispatched workflow cannot publish a release.

The selected no-cost trust model is intentionally transparent: HandleScope is
not Authenticode-signed. The release workflow instead produces an exact
portable ZIP, a SHA-256 manifest, an SPDX 2.3 SBOM, an immutable compatibility
manifest, and GitHub artifact attestations. It uses no certificate, external
signing account, paid service, long-lived credential, or repository secret.
Users must verify downloads and may see **Unknown publisher** or SmartScreen
warnings on Windows.

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

5. Review the exact staged inventory and test both applications on a disposable
   Windows x64 machine. Destructive tests may target only test-owned processes
   and handles.
6. Merge through protected `main`. From a clean, synchronized `main`, create
   and push one annotated tag:

   ```powershell
   git tag -a v<version> -m "HandleScope <version>"
   git push origin v<version>
   ```

7. Review and approve the pending `release` environment deployment. Do not
   approve if the tag, commit, workflow, or generated inventory is unexpected.

### SessionDock managed-setup contract

HandleScope v0.2.1 introduces an immutable per-release compatibility manifest;
it does not grant a floating authorization to future HandleScope or SessionDock
versions. Before a compatible SessionDock release adds a catalog entry, verify
the manifest against the public assets, then review SessionDock's signed catalog
sequence and validity, compatible version range, compiled protocol adapters,
confirmation, canonical asset URLs, fixed sizes and SHA-256 hashes, redirect
allowlist, streamed bounds, checksum parsing, ZIP and internal inventory
validation, installer arguments, PowerShell policy scope, standard-user token,
cancellation behavior, downgrade refusal, and separate integration opt-in against
[`integrations/sessiondock.md`](integrations/sessiondock.md).

Record the new HandleScope tag, protected-main source commit, ZIP, checksum,
release-manifest asset names, lengths, and SHA-256 hashes, extracted API
executable length and SHA-256 hash, protocol/capability lists, SBOM identity,
release immutability, and successful artifact attestation verification. A
SessionDock catalog may be updated only after the new HandleScope release is
public and these values have been independently checked. Never revise the
contract of an existing immutable release to authorize a client retroactively.

No cryptographic tag key is required. Repository and tag rules determine who
may create release tags, while the GitHub attestation binds each asset to the
tag-triggered workflow and source repository.

## Automated release flow

The tag-triggered workflow:

1. verifies the repository, annotated tag, project version, release notes, and
   exact protected-main tip;
2. builds and runs the controlled integration harness with the pinned .NET SDK,
   locked restore, and NuGet.org as the only package source;
3. publishes two compressed, self-contained Windows x64 single-file
   executables without debug symbols and records their embedded .NET runtime
   components;
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
