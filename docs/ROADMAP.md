# Roadmap

Status legend: 🟢 released · 🟡 built, hardware test pending · ⚪ planned · ❌ dropped

RigShift is meant to stay small: switch into the rig and back, reliably.

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

## v1.1 / v1.2 – triggering and control

- 🟢 Global hotkeys per profile (1.2.0)
- 🟢 `rigshift://apply/<name>` URI scheme (1.2.0)
- 🟢 Recording device and separate communications role per profile (1.0)

## v1.3 – getting into the rig

- 🟢 Volume per profile (playback and recording)
- 🟢 Launch / stop apps per profile (SimHub, Crew Chief, …), optionally after a USB device is detected
- 🟢 USB device triggers (wheelbase, headset dongle) with a delay per rule before switching back
- 🟡 Keep awake per profile: no sleep, screen saver or display timeout
- 🟡 HDR and refresh rate per display – HDR over HDMI froze the graphics driver of the test PC, also from the Windows
  settings; 1.4.1 guards the call
- 🟢 Windows left on switched-off screens move to the main screen
- 🟢 Warning when Windows may power down a USB trigger device
- 🟡 Game sound stays loud during calls (Windows communications ducking off per profile)
- 🟢 Custom monitor names, Displays and About & help pages

## v1.4 – polish after the first hardware round

- 🟢 Automation rules with several USB devices and custom device names (1.4.0)
- 🟢 Apps start after the switch; hotkeys and automation keep working while they wait for their device (1.4.0)
- 🟢 Diagnostic info without user name and full device paths (1.4.0, 1.4.1)
- 🟡 Ask to switch on a monitor that is off and wait for it (1.4.1)
- 🟡 Known USB devices and refresh rates stay selectable while the device or display is off (1.4.1)
- ⚪ Display numbers as in the Windows settings
- ⚪ App picker with installed and running programs instead of a file dialog
- ⚪ Offer to apply audio and apps when Windows itself restores a profile's display layout

Dropped after a scope review (the tool should stay small):

- ❌ Process triggers with game templates – the switch came too late, while the game was already starting
- ❌ Power plan per profile – no demand
- ❌ Local HTTP API – no demand; the CLI, hotkeys and `rigshift://` links cover scripts and Stream Deck
- ❌ Home Assistant via MQTT discovery – no demand
- ❌ Focus assist, game mode, night light – no official API

## v2 – community

- Setup wizard for first-time users
- winget
- AMD / Intel validation, per-GPU head-budget presets
