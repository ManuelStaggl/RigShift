# Code signing policy

**Status:** the application is being prepared. Until it is approved, releases are not signed; verify them with the build
provenance attestation and `SHA256SUMS` (see [SECURITY.md](../SECURITY.md#updates-and-their-residual-risk)).

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## Team

| Role | Who |
|---|---|
| Author, reviewer, approver | [Manuel Staggl](https://github.com/ManuelStaggl) |

Changes from anyone else reach `main` only through a pull request that the author has reviewed. Every signing request
is approved by hand. The GitHub account and the SignPath account use multi-factor authentication.

## What gets signed

Only what the `release.yml` workflow of this repository builds on GitHub-hosted runners from a `v*` tag on `main`:
`RigShift.exe`, the RigShift assemblies, `Update.exe`, `RigShift-win-Setup.exe`. Nothing built on a developer PC is
ever signed. Tags and `main` are protected against moves, deletion and force pushes (`.github/rulesets`).

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user
or the person installing or operating it – with one exception: the update check at GitHub, which an administrator can
switch off. Details: [PRIVACY.md](../PRIVACY.md).
