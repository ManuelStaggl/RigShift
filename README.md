<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/rigshift-horizontal-color-dark-tagline.svg">
    <img alt="RigShift – Desk to rig. In one shift." src="docs/brand/rigshift-horizontal-color-light-tagline.svg" width="420">
  </picture>
</h1>

[![CI](https://github.com/ManuelStaggl/RigShift/actions/workflows/ci.yml/badge.svg)](https://github.com/ManuelStaggl/RigShift/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**One click between desk and sim rig.** RigShift is a small Windows tray app that switches your complete
display topology *and* audio devices atomically – built for sim racers who share one PC between a multi-monitor
desk and an ultrawide / triple-screen / VR cockpit.

> Status: **early development (milestone M4.5 – tray app, command line, profile editor and brand identity done).** Not yet tested
> on real multi-monitor hardware, no release yet. Watch the repo or check the [roadmap](docs/ROADMAP.md).

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: light)" srcset="docs/screenshots/profiles-light.png">
    <img alt="RigShift main window with a Desk and a Rig profile" src="docs/screenshots/profiles-dark.png" width="820">
  </picture>
</p>

## Why another display switcher?

Because the existing ones fail on modern GPUs. NVIDIA cards have a fixed budget of display heads, and
high-bandwidth displays (4K@165 Hz, 5120×1440@240 Hz) consume two each. Tools that enable and disable monitors
one at a time run out of heads mid-sequence and error out. RigShift hands the *entire* target topology to
Windows in a single `SetDisplayConfig` call – the only approach that works on that hardware.

Beyond that, RigShift is designed to be boring in the good way:

- **Stable display identity** via device paths and EDID, so profiles survive reboots, driver updates and
  reconnects (including virtual displays such as spacedesk).
- **Waits instead of failing** when a monitor is still waking up over HDMI or a wireless display is not connected
  yet; optional displays are picked up as soon as they appear.
- **Safety net:** if you don't confirm the new layout within a few seconds (because there is no picture),
  RigShift rolls back to the previous one.
- **Audio included:** default playback and communications devices per profile.
- **Explains problems** ("this combination exceeds your GPU's display heads") instead of showing error 31.
- **Scriptable:** `RigShift.exe apply Rig` from a Stream Deck, button box, SimHub or shortcut.

## Screenshots

| | |
|---|---|
| <img alt="Tray popup for switching profiles" src="docs/screenshots/tray-popup-dark.png" width="400"> | <img alt="Confirmation dialog with countdown" src="docs/screenshots/confirmation-dark.png" width="400"> |
| **Tray popup** – switch profiles from the notification area. | **Safety net** – keep the new layout or it reverts on its own. |
| <img alt="Profile editor with displays and audio devices" src="docs/screenshots/profile-editor-dark.png" width="400"> | <img alt="Settings page" src="docs/screenshots/settings-dark.png" width="400"> |
| **Profile editor** – main display, optional displays, audio devices. | **Settings** – default profile, autostart, confirmation time, language. |

## Planned features

See [docs/ROADMAP.md](docs/ROADMAP.md). Highlights after 1.0: hotkeys, `rigshift://` links, per-profile apps,
process and USB triggers with game templates (LMU, iRacing, ACC, AC EVO, …), race mode (no notifications,
no sleep), power plan, HDR, local HTTP API and Home Assistant integration.

## Command line

```bat
RigShift.exe apply <name> [--no-confirm] [--dry-run]
RigShift.exe list
RigShift.exe save <name>
RigShift.exe status
```

Names are not case-sensitive. `save` stores the current display arrangement and default playback device; an
existing profile with that name is updated. `apply` and `save` start RigShift in the tray if it is not running and
wait for the result; the easiest way to get a ready-made shortcut (also for a Stream Deck "Open" action) is
**⋯ → Create desktop shortcut** on a profile.

| Exit code | Meaning |
|---|---|
| 0 | Applied (optional displays may be missing), or command succeeded |
| 1 | Failed, or another switch is running |
| 2 | Blocked: a required display is missing, nothing changed |
| 3 | Not confirmed, previous arrangement restored |
| 4 | Profile not found |
| 5 | Invalid arguments |

RigShift.exe is a Windows GUI program, so shells do not wait for it. To see output and exit code in a console,
run `start /wait RigShift.exe status` (cmd) or `Start-Process RigShift.exe status -Wait -NoNewWindow`
(PowerShell).

## Requirements

- Windows 10 (2004+) or Windows 11, x64
- Any GPU – the Windows CCD API is vendor-neutral. Head-limit heuristics are tuned for NVIDIA first;
  AMD/Intel feedback is welcome.
- No admin rights needed.

## Building

```bash
dotnet build RigShift.slnx
dotnet test --solution RigShift.slnx
```

Requires the .NET 10 SDK (see `global.json`). Windows is needed to build the app; the core library and its
tests build anywhere.

## Project layout

| Path | Content |
|---|---|
| `src/RigShift.Core` | Domain logic: profiles, topology planner, switch orchestration – no Win32 |
| `src/RigShift.Windows` | CCD API, Core Audio, `IPolicyConfig`, device notifications (CsWin32) |
| `src/RigShift.App` | WPF tray app (WPF-UI), DI host, CLI |
| `tests/` | Unit tests (xunit v3) |
| `docs/` | [Architecture](docs/ARCHITECTURE.md), [display topology rules](docs/display-topology.md), [decisions](docs/decisions/), roadmap |
| `legacy/` | The PowerShell script RigShift replaces – proven reference implementation |

## Related projects

- [DisplayProfileManager](https://github.com/zac15987/DisplayProfileManager) – WPF, .NET Framework 4.8, staged (non-atomic) application, on hold
- [MonitorSwitcher](https://github.com/fernandoenzo/MonitorSwitcher) – tray tool for exclusive monitor switching
- [DisplayMagician](https://github.com/terrymacdonald/DisplayMagician) – game-launcher-centric, NVIDIA/AMD APIs

RigShift focuses on the atomic switch, robustness on real hardware and sim-racing workflows.

## Contributing

Issues and pull requests are welcome – see [CONTRIBUTING.md](CONTRIBUTING.md). Please attach the log from
`%LocalAppData%\RigShift\logs` to bug reports (it contains display device paths, nothing else personal).

## License

[MIT](LICENSE) © 2026 Manuel Staggl
