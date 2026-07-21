# Changelog

Notable user-visible and security-relevant changes are documented here.

## Unreleased

- No changes yet.

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
