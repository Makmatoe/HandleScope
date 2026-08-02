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
label the binaries as **Unknown publisher**, and SmartScreen, Smart App Control,
or antivirus reputation policy may warn or refuse execution. Verification does
not grant permission to disable those controls. The application has no
self-updater, so installing a newer version is an explicit user action.

## Current security boundary

The desktop, headless API, and native setup tool run as the current standard
user (`asInvoker`). They do not request elevation or enable `SeDebugPrivilege`.
The setup tool accepts only its fixed local lifecycle commands, verifies the
complete release inventory before installation, and does not launch PowerShell
or change execution policy. The desktop limits targets to non-elevated processes
owned by the same user in the same Windows session.

Security products can attach named data streams to downloaded files. Native
setup accepts only a small, bounded set of well-formed source-only metadata
streams, validates `Zone.Identifier` separately, and treats every named stream
as untrusted. Integrity hashes and installation copies use only the locked
unnamed data stream. Named streams are never copied from the release; staged and
installed files must contain only unnamed data or setup fails closed.

The API additionally refuses elevated, service-account, and session-0
execution. It listens only on IPv4 loopback and authenticates protected
endpoints with a rotating 256-bit token. Standalone mode stores that token under
the current user's protected local application-data directory. SessionDock 3.0
included mode transfers it through an inherited anonymous pipe and retains it
only in parent/child memory. Both enforce the compiled
`roblox-singleton-event-v1` policy. A successful five-second dry run creates a
single-use execution plan with a random identifier that the identical execution
request must present; process and handle identities are revalidated before
closure.

These controls narrow the impact but do not turn localhost into a security
principal. Malicious software already running as the same Windows user may be
able to read the token and request the one allowed Roblox operation. Closing the
allowed event may destabilize Roblox. Review the complete
[`threat model`](docs/THREAT_MODEL.md) before integrating the API.

SessionDock 3.0 synchronizes reviewed HandleScope 0.3.0 source from an immutable
tag/commit, records the allowlisted paths and hashes in
`SessionDock.HandleScope/handlescope-upstream.json`, and compiles it into
`SessionDock.exe`. The included child is non-elevated, parent-owned,
loopback-only, pipe-bootstrapped, and parent-lifetime-bound. There is no separate
HandleScope executable, installer, PowerShell command, UAC prompt, scheduled
task, autostart entry, update, or uninstall operation in that flow.

**Standalone HandleScope (advanced)** remains independently installed and
managed. SessionDock may connect to an already running compatible API after
strict discovery, process, metadata, and policy checks, but it must never
download, install, start, stop, update, downgrade, reconfigure, or uninstall the
standalone application. The signed compatibility catalog remains for older
SessionDock clients and reviewed advanced-standalone identities; it cannot
define executable paths, arguments, endpoints, or API behavior, and the 3.0
included flow does not execute from it.

Elevation, silent standalone lifecycle changes, mutable unauthenticated
downloads, downgrades, `Bypass`, `Unrestricted`, saved policy changes, and Group
Policy overrides remain outside the supported boundary.

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
installation or autostart, included-source provenance, parent/pipe lifecycle,
integrity, or release delivery require focused security review and the
controlled integration harness. Shared engine changes must land in this
repository first and be synchronized into SessionDock with matching current
documents, license/notices/SBOM, and provenance. Never weaken a
fail-closed release check to make publication succeed, and never test
destructive operations against processes or files you do not own.
