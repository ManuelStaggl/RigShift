# Security policy

## Supported versions

Only the latest release gets fixes. RigShift updates itself, so staying current needs no action.

## Reporting a vulnerability

Please do not open a public issue. Report it privately via
[**Security → Report a vulnerability**](https://github.com/ManuelStaggl/RigShift/security/advisories/new) instead.
Include the version, what an attacker can do, and steps to reproduce.

You will get an answer within a week. Once a fix is released, the advisory is published with credit to you, unless
you prefer otherwise.

## Updates and their residual risk

- **Where updates come from.** RigShift uses [Velopack](https://velopack.io) and checks the
  [GitHub releases](https://github.com/ManuelStaggl/RigShift/releases) of this repository. A release is built and
  uploaded by the `release.yml` GitHub Actions workflow when a version tag is pushed. A downloaded update is applied
  at the next start of RigShift, unless updates are set to notify only.
- **What is checked.** Velopack verifies the SHA-256 hash of every package against the release feed it downloaded from
  the same GitHub release. That protects against broken or truncated downloads, not against a tampered release.
- **What you can check yourself** (releases after 3.5.1). Every release carries `SHA256SUMS`, a CycloneDX SBOM
  (`RigShift-<version>.cdx.json`) and a build provenance attestation: a signed statement that the file was built by
  this repository's release workflow from the tagged commit. Verify a download with
  `gh attestation verify RigShift-win-Setup.exe --repo ManuelStaggl/RigShift`. The `main` branch and the `v*` tags
  are protected against force pushes, moves and deletion (`.github/rulesets`).
- **No code signing.** The installer, the executable and the update packages are not signed (a deliberate decision
  for a free hobby project). Windows SmartScreen may warn on the first install, and there is no signature that would
  expose a manipulated update.
- **Residual risk.** The trust anchor is the maintainer's GitHub account and the release workflow. Whoever gains write
  access to the repository, its tags or its releases – or compromises an action the workflow uses – can ship an
  update that every installation applies automatically. This is mitigated by two-factor authentication on the account
  and by keeping the workflow small; it is not eliminated.
- **If you want more control**, set updates to notify only in the settings and install a release after reviewing it,
  or build RigShift from source.

## Scope

RigShift runs without administrator rights and talks to nothing but GitHub (update checks). Relevant areas are
the named pipe used by the command line, the `rigshift://` link handler, programs started by profiles and
automation rules, and the update process.
