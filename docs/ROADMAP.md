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
- 🟡 Volume per profile (playback and recording)
- 🟡 Launch / stop apps per profile (SimHub, Crew Chief, …)
- 🟡 Process triggers with game templates (LMU, iRacing, ACC, AC EVO, AC Rally, rFactor 2, AMS2, RaceRoom, F1,
  Forza Horizon 6)

## v1.2 – automation and comfort

- 🟡 USB device triggers (wheelbase, headset dongle)
- 🟡 Keep awake per profile: no sleep, screen saver or display timeout (focus assist and game mode dropped: no
  official API, and Windows handles both for full-screen games)
- 🟡 Power plan per profile, the previous plan comes back on switch back
- HDR and refresh rate per display; night light (experimental)
- 🟡 Local HTTP API for scripts, SimHub, Stream Deck ([docs](http-api.md))
- Home Assistant via MQTT discovery

## v2 – community

- Setup wizard for first-time users
- Anonymised diagnostics export for issues
- Code signing and winget
- AMD / Intel validation, per-GPU head-budget presets
- Dry-run mode in the UI
