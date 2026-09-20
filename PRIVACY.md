# Privacy

RigShift has no account, no telemetry, no analytics and no crash upload. Everything it stores stays on your PC.

## The one network connection

RigShift (installed or portable) asks GitHub for new releases of this repository: at the start, then every 24 hours
(sooner after a failed attempt or after standby), and when you press "Check for updates". If there is a newer version, it downloads the package from the same GitHub
release. GitHub sees what every web request shows – your IP address, the time and a user agent – and handles it under
the [GitHub privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
RigShift sends nothing else: no identifier, no profile data, no hardware data.

Builds from source never check. "Only notify about updates" in the settings stops the
download, not the check.

## What stays on your PC

| What | Where |
|---|---|
| Profiles, games, settings | `%AppData%\RigShift` |
| Logs (device names, program paths, no audio endpoint IDs) | `%AppData%\RigShift\logs`, 14 days |
| Backups | wherever you save the ZIP |

To find installed games RigShift reads the library files of Steam and Epic on your disk; it does not talk to either
service.

## What leaves only when you send it

"Copy diagnostics" puts a text with your monitor names, connectors, GPU and RigShift version on the clipboard. Read it
before you paste it into a public issue. Logs and backups leave your PC only if you attach them yourself.

## Contact

Questions: open an issue or use the contact in [SECURITY.md](SECURITY.md).
