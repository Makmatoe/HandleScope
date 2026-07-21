## Summary

Describe the user-visible change and link the approved issue.

## Safety and privacy

- [ ] I did not add credentials, connection documents, bearer tokens, signing
      material, process dumps, personal paths, or generated release artifacts.
- [ ] Process selection, handle selection, elevation, persistence, network,
      logging, dependency, installer, and release changes are explained below.
- [ ] Destructive tests target only a child process and objects created by the
      test itself.
- [ ] The local API remains loopback-only and the compiled public automation
      policy is not broadened by configuration or client input.

Safety/privacy notes:

## Validation

- [ ] `./scripts/Build.ps1 -CI`
- [ ] Release metadata or packaging checks, if relevant
- [ ] Relevant behavior was exercised on a supported Windows x64 system
- [ ] Documentation and tests were updated where needed

Validation details:

## Sanitized evidence

Include screenshots only when useful. Remove usernames, process identifiers,
handle names and values, local paths, session identifiers, and all credentials.
