# Roadmap

Status legend: 🟢 released · 🟡 built, hardware test pending · ⚪ planned

## v1.0 – replace the script, small and stable

| Milestone | Status | Scope |
|---|---|---|
| M0 Skeleton | 🟢 | Solution, projects, domain model, interfaces, legacy `.display` parser, CI |
| M1 Core logic | 🟢 | `TopologyPlanner`, `SwitchOrchestrator`, head-budget heuristic, wait-for-target retry, rollback decision – all unit-tested with a fake configurator |
| M2 Windows layer | 🟢 | CCD query/apply, Core Audio + `IPolicyConfig`, JSON profile store, legacy import |
| M3 App | 🟢 | Tray shell, profile list, confirm countdown, toasts, settings (default profile, autostart, timeout, language), diagnostics page |
| M4 CLI & instance | 🟢 | `RigShift.exe apply …`, named-pipe forwarding, autostart, "save current layout as profile", profile editor |
| M5 Hardware validation | 🟢 | Desk ↔ rig, sleeping HDMI monitor (error 31), missing spacedesk + follow-up, rollback on no confirmation |
| M6 Release 1.0 | 🟢 | Velopack package, release workflow, README with screenshots, changelog |

## v1.1 – triggering and control

- 🟢 Global hotkeys per profile (1.2.0)
- 🟢 `rigshift://apply/<name>` URI scheme (1.2.0)
- 🟢 Recording device and separate communications role per profile (1.0)
- 🟢 Volume per profile (playback and recording) (1.3.0)
- 🟢 Launch / stop apps per profile (SimHub, Crew Chief, …) (1.3.0)
- 🟢 Process triggers with game templates (LMU, iRacing, ACC, AC EVO, rFactor 2, AMS2, F1) (1.3.0)

## v1.2 – automation and comfort

- USB device triggers (wheelbase, headset dongle)
- Race mode: focus assist on, game mode, no sleep/screensaver – restored on switch back
- Power plan per profile
- HDR and refresh rate per display; night light (experimental)
- Local HTTP API for scripts, SimHub, Stream Deck
- Home Assistant via MQTT discovery

## v2 – community

- Setup wizard for first-time users
- Anonymised diagnostics export for issues
- Code signing and winget
- AMD / Intel validation, per-GPU head-budget presets
- Dry-run mode in the UI
