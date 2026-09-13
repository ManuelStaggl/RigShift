# Contributing to RigShift

Thanks for helping. A few ground rules keep the project small and reliable.

## Before you start

- Read [docs/display-topology.md](docs/display-topology.md). It explains why the app does things the way it
  does; changes that violate those rules will not be merged.
- Check the [roadmap](docs/ROADMAP.md) and open issues. For anything larger than a bug fix, open an issue first.

## Development

```bash
dotnet build RigShift.slnx
dotnet test --solution RigShift.slnx
```

- .NET 10 SDK (`global.json`), Windows for the app project.
- Warnings are errors. Analyzers run at `latest-recommended`.
- Logic goes into `RigShift.Core` with tests. Win32 goes into `RigShift.Windows` behind a Core interface.
- Structured logging only (`Log.Information("Profile {Name}", name)`), never string interpolation in log calls.
- Pass `CancellationToken` through async chains; never block the UI thread.

## Commits and pull requests

- [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `refactor:`, `docs:`, `chore:`, `test:`.
- One topic per PR. Update `CHANGELOG.md` under *Unreleased*.
- CI must be green.

## Bug reports

Please include the log from `%AppData%\RigShift\logs`, your GPU and driver version, and the display
configuration (resolution/refresh per monitor). Device paths in the log are not personal data, but feel free to
redact them.

## Game templates (v1.1+)

Process-trigger templates live in `templates/` as JSON. Adding a sim is a documentation-level PR.
