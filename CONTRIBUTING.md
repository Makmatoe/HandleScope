# Contributing

HandleScope is Windows-specific systems software. A small selector mistake can
close the wrong process handle, so changes should be narrow, reviewable, and
verified against controlled child processes only.

## Before changing code

1. Read [`SECURITY.md`](SECURITY.md) and
   [`docs/THREAT_MODEL.md`](docs/THREAT_MODEL.md).
2. Use Windows 10 or 11 with the SDK selected by [`global.json`](global.json).
3. Do not add a package when the .NET or Windows platform already provides the
   needed capability.
4. Do not commit connection documents, tokens, logs, signing material, process
   dumps, build output, or machine-specific paths.
5. Treat this repository as the source of truth for the HandleScope engine
   synchronized into SessionDock. Shared Core/API changes must land here first;
   update the SessionDock snapshot/provenance and both repositories' current
   integration/security/privacy documentation in a coordinated follow-up.

## Validate a change

From the repository root:

```powershell
.\scripts\Build.ps1 -CI
```

That command verifies repository hygiene, restores and builds the solution with
warnings treated as errors, and runs the controlled integration harness. The
harness may close handles only in the child process it creates.

For documentation-only work, the minimum check is:

```powershell
.\scripts\Verify-Repository.ps1
```

## Pull requests

- Explain the user-visible and security impact.
- Call out API-contract changes explicitly and retain v1 compatibility unless
  a migration has been approved.
- Include validation output and any Windows-version limitations.
- Describe whether SessionDock's included source must be resynchronized and
  name the upstream tag/commit that will be pinned.
- Preserve SessionDock's independent source, standalone runtime-version, and API
  selectors. Automatic/Keep installed/exact reviewed requirements, stale-pin
  recovery, explicit standalone-only catalog refresh, and local-only panel
  opening are backwards-compatibility and security contracts, not installer or
  lifecycle features.
- Keep generated output out of the commit.
- Release changes must remain fail-closed: preserve locked restore, pinned
  actions, protected publication, exact artifact inventories, version/tag
  checks, checksums, SBOM generation, GitHub attestations, fresh-only drafts,
  and downloaded-asset revalidation before publication.
