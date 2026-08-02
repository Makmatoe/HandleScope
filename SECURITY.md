# Security policy

HandleScope inspects and can close handles owned by other Windows processes.
That capability is inherently destructive and requires unusually careful
review.

## Supported releases

Security fixes target the latest published, stable HandleScope release. Build
artifacts, portable source builds, forks, and older releases may receive only
best-effort support. Obtain releases from the project's GitHub Releases page
and verify them using [`docs/VERIFY_DOWNLOAD.md`](docs/VERIFY_DOWNLOAD.md).

Official releases intentionally use no Authenticode certificate or paid
signing service. They include an exact portable ZIP, SHA-256 manifests, an SPDX
SBOM, an immutable compatibility manifest, and GitHub artifact attestations.
Verify those materials using
[`docs/VERIFY_DOWNLOAD.md`](docs/VERIFY_DOWNLOAD.md); Windows may correctly
label the binaries as **Unknown publisher**. The application has no
self-updater, so installing a newer version is an explicit user action.

## Current security boundary

The desktop and headless API run as the current standard user (`asInvoker`).
They do not request elevation or enable `SeDebugPrivilege`. The desktop limits
targets to non-elevated processes owned by the same user in the same Windows
session.

The API additionally refuses elevated, service-account, and session-0
execution. It listens only on IPv4 loopback, authenticates protected endpoints
with a rotating 256-bit token stored under the current user's protected local
application-data directory, and enforces the compiled
`roblox-singleton-event-v1` policy. A successful five-second dry run creates a
single-use execution plan with a random identifier that the identical execution
request must present; process and handle identities are revalidated before
closure.

These controls narrow the impact but do not turn localhost into a security
principal. Malicious software already running as the same Windows user may be
able to read the token and request the one allowed Roblox operation. Closing the
allowed event may destabilize Roblox. Review the complete
[`threat model`](docs/THREAT_MODEL.md) before integrating the API.

HandleScope v0.2.1 also defines a dynamic but constrained delivery boundary for
compatible SessionDock releases. SessionDock may select only releases whose
package, checksum, release manifest, installed executable, protocol contracts,
capabilities, and SessionDock version range are bound by its signed,
rollback-resistant compatibility catalog. A managed setup is authorized only
after a dedicated, version-specific user confirmation and complete verification
of the selected immutable release. It must run the unmodified installer as the
standard user, verify before installing, disclose immediate startup and limited
autostart, and leave the SessionDock opt-in separate. Catalog metadata cannot
define executable API behavior; only code-reviewed local adapters can do so.
Elevation, silent lifecycle changes, mutable unauthenticated downloads,
downgrades, `Bypass`, `Unrestricted`, saved policy changes, and Group Policy
overrides remain outside the supported boundary.

## Reporting a vulnerability

Do not disclose a suspected vulnerability in a public issue, pull request,
discussion, log, or screenshot. Use GitHub's private security-advisory form:

<https://github.com/Makmatoe/HandleScope/security/advisories/new>

Include the affected version or revision, Windows version, reproduction steps,
expected and observed behavior, and the minimum proof needed to demonstrate
impact. Remove bearer tokens, usernames, process dumps, handle names, local
paths, and unrelated machine data.

The maintainer will acknowledge a report when possible, reproduce it in a
controlled environment, and coordinate disclosure only after a fix or an
explicit risk decision. No bounty or response-time guarantee is currently
offered.

## Security-sensitive changes

Changes involving manifests or execution level, connection-file ACLs,
authentication, process identity, executable verification, the compiled
automation policy, dry-run plans, handle matching or closure, per-user
installation or autostart, integrity, or release delivery require focused
security review and the controlled integration harness. Never weaken a
fail-closed release check to make publication succeed, and never test
destructive operations against processes or files you do not own.
