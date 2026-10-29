# Security policy

## Supported versions

Only the latest release gets fixes. RigShift updates itself, so staying current needs no action.

RigShift brings its own .NET runtime. When Microsoft ships a .NET security update, a RigShift patch release with the
new runtime follows soon after.

## Reporting a vulnerability

Please do not open a public issue. Report it privately via
[**Security → Report a vulnerability**](https://github.com/ManuelStaggl/RigShift/security/advisories/new) instead.
Include the version, what an attacker can do, and steps to reproduce.

You will get an answer within a week. Once a fix is released, the advisory is published with credit to you, unless
you prefer otherwise.

## Updates

- Updates come from the [GitHub releases](https://github.com/ManuelStaggl/RigShift/releases) of this repository,
  built by the `release.yml` workflow from a version tag on `main`, and are installed at the next start of RigShift.
- Velopack checks the SHA-256 hash of every package against the release feed. That catches broken downloads, not a
  tampered release.
- Each release carries `SHA256SUMS`, an SBOM and a build attestation; check a download with
  `gh attestation verify RigShift-win-Setup.exe --repo ManuelStaggl/RigShift`.
- The files are not code-signed. Whoever gains write access to this repository could ship an update; if that worries
  you, set updates to notify only and install a release after looking at it, or build from source.

## Scope

RigShift runs without administrator rights and talks to nothing but GitHub (update checks). Relevant areas are
the named pipe used by the command line, the `rigshift://` link handler, programs started by profiles and
automation rules, and the update process.
