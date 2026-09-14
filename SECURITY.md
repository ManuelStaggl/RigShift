# Security policy

## Supported versions

Only the latest release gets fixes. RigShift updates itself, so staying current needs no action.

## Reporting a vulnerability

Please do not open a public issue. Report it privately via
[**Security → Report a vulnerability**](https://github.com/ManuelStaggl/RigShift/security/advisories/new) instead.
Include the version, what an attacker can do, and steps to reproduce.

You will get an answer within a week. Once a fix is released, the advisory is published with credit to you, unless
you prefer otherwise.

## Scope

RigShift runs without administrator rights and talks to nothing but GitHub (update checks). Relevant areas are
the named pipe used by the command line, the `rigshift://` link handler, programs started by profiles and
automation rules, and the update process.
