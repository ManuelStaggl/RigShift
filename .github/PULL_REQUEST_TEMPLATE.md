## What

<!-- One paragraph: what changes and why. -->

Closes #

## Testing

<!-- How you checked it: tests added or changed, manual steps, hardware it ran on. -->

## Screenshots

<!-- UI changes only: before and after. Delete this section otherwise. -->

| Before | After |
|---|---|
|  |  |

## Checklist

- [ ] Follows the rules in `docs/display-topology.md` (atomic switch, no persisted LUIDs/target IDs)
- [ ] Logic lives in `RigShift.Core` and has tests; Win32 stays behind a Core interface
- [ ] `dotnet build` and `dotnet test --solution RigShift.slnx` are green with warnings as errors
- [ ] `CHANGELOG.md` updated under *Unreleased* (user-visible changes only)
- [ ] Conventional Commit message
