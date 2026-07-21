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
- Keep generated output out of the commit.
- Release changes must remain fail-closed: preserve locked restore, pinned
  actions, protected publication, exact artifact inventories, version/tag
  checks, checksums, SBOM generation, GitHub attestations, fresh-only drafts,
  and downloaded-asset revalidation before publication.
