# Changelog

Notable user-visible and security-relevant changes are documented here.

## Unreleased

- No changes yet.

## 0.1.2 - 2026-07-21

- The API installer now removes inherited Windows download markers only after
  each installed file matches the reviewed release manifest, preventing
  verified installed scripts from being blocked by `RemoteSigned`.
- Added `-EnableSessionDock` to the API installer so installation, startup,
  optional autostart, and explicit SessionDock opt-in can be completed with one
  command.
- Corrected the integration helper and documentation to use the canonical
  `%LOCALAPPDATA%\SessionDock\handlescope.json` location and current SessionDock
  repository. An older minimal opt-in is copied only when the canonical file is
  absent; legacy data is preserved and cannot overwrite canonical settings.
- Expanded Windows PowerShell regression coverage for canonical precedence,
  safe legacy copying, explicit replacement, and temporary-file cleanup.

## 0.1.1 - 2026-07-21

- Fixed the SessionDock opt-in helper when it is run again after the minimal
  `{"enabled":true}` setting already exists in Windows PowerShell 5.1 strict
  mode.
- Added an explicit Windows PowerShell compatibility regression test for the
  minimal SessionDock setting check.
- Clarified that release downloads must use a dedicated directory and made
  checksum verification distinguish a missing entry from a hash mismatch.

## 0.1.0 - 2026-07-21

- Initial reviewed Windows x64 release structure.
- Added a standard-user desktop handle inspector limited to non-elevated,
  same-user, same-session processes.
- Added a standard-user local API v1 that refuses elevated, service-account,
  and session-0 execution and enforces the compiled Roblox singleton policy.
- Added short-lived, single-use dry-run plans with process and handle identity
  revalidation before closure.
- Added a no-UAC per-user API installer with optional limited per-SID autostart.
- Added a no-cost, tag-gated release pipeline with exact inventories, SHA-256
  checksums, an SPDX SBOM, protected publication, and GitHub attestations.
- Added an explicit, minimal SessionDock integration helper with safe overwrite
  behavior.
- Documented local-only data handling, no telemetry or updater, and .NET native
  single-file extraction under the current user's temporary directory.
