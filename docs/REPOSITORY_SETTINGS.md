# Recommended GitHub repository settings

These controls complement the checked-in workflows and should be configured
before the first public release.

## General and Actions

- Keep `main` as the default branch; allow squash merges only, require web
  commit signoff, and automatically delete merged topic branches.
- Set default workflow permissions to **Read repository contents** and prevent
  workflows from creating or approving pull requests.
- Allow only the Actions used by this repository. Every external Action is
  pinned to a full commit SHA.
- Enable the dependency graph, Dependabot alerts and security updates, secret
  scanning, push protection, private vulnerability reporting, and CodeQL.
- Set the repository variable `DEPENDENCY_REVIEW_ENABLED` to `true` once the
  dependency graph is available.
- Enable immutable releases. Disable unused Pages and Wiki features.

## `main` branch ruleset

Create an active, non-bypassable ruleset targeting `main` that:

- blocks deletion and force pushes;
- requires pull requests, conversation resolution, strict status checks, and
  linear history;
- allows squash merge only;
- requires **Build and controlled integration test**, **Dependency review**,
  and **Analyze C#** after those checks have completed successfully on `main`;
- dismisses stale reviews when new commits are pushed.

This is currently a single-maintainer project, so choose zero mandatory
approvals instead of creating a permanent release deadlock. Revisit that choice
when an independent maintainer is available. Keep any emergency bypass actor
explicit and audit every use.

## Release-tag ruleset

Create a second active, non-bypassable ruleset for `v*` tags. Restrict tag
creation to the maintainer, and block update, deletion, and non-fast-forward
operations. A published version must never be retagged.

The workflow requires an annotated tag at the exact current `origin/main` tip,
but it does not require a GPG/SSH tag signature or any paid certificate. GitHub
artifact attestations bind release assets to the repository workflow instead.

## `release` environment

Create one environment named `release` and restrict deployment branches/tags to
protected `v*` tags. Add `Makmatoe` as the required reviewer and disable
administrator bypass. Because the repository currently has one maintainer,
leave self-review enabled so a legitimate release can be approved without a
second account.

Only the final publication job uses this environment. It requires no variables
and no secrets. The workflow grants that job only the permissions needed to
create a GitHub Release and artifact attestations; compilation remains in a
separate read-only job.

Review effective rules, security alerts, environment history, and the `main`
tip before creating each release tag. A workflow file cannot enforce repository
settings that were never enabled.
