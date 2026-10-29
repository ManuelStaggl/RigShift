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

## From issue to release

1. Every change starts as an issue. Feature requests use the form, which doubles as the spec: problem, acceptance
   criteria, non-goals and test plan. Work is tracked on the [roadmap board](https://github.com/users/ManuelStaggl/projects/1)
   (Backlog → Ready → In Progress → In Review → Done).
2. One issue, one branch: `feat/<short-name>`, `fix/<short-name>`, `docs/…` or `chore/…`, cut from `main`.
3. Open a pull request with `Closes #<issue>` and fill in the template. UI changes need before and after screenshots.
4. `main` only takes pull requests with a green `build` check. Pull requests are merged with a merge commit.
5. What is finished gets released; the version and milestone are set on the issue.
6. RigShift ships its own .NET runtime. When a merged `dotnet-sdk` update brings a runtime with security fixes, a patch
   release follows within a week; other runtime updates ship with the next release.

## Commits

- [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `refactor:`, `docs:`, `chore:`, `test:`, `ci:`.
- One topic per PR. Update `CHANGELOG.md` under *Unreleased* for anything a user notices.

## Labels

`type:*` says what kind of work it is, `area:*` where it lands (display, audio, games, ui, cli, update, infra),
`priority:*` how soon. `good first issue` marks small, well-described tasks.

## Bug reports

In RigShift, **Help → Save support package** saves a ZIP with the diagnostics (GPU, driver, displays, audio), the
last three logs and your profiles, without your Windows user name, and opens the bug report form to attach it to.

## Code of conduct and security

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report security problems privately as described in
[SECURITY.md](SECURITY.md), not as an issue.
