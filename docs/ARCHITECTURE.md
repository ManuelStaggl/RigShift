# Architecture

## Projects

| Project | Target | Role |
|---|---|---|
| `src/RigShift.Core` | `net10.0` | Domain: profiles, topology planning, switch orchestration, game entries and sessions, automation rules, CLI parsing and command runner, pipe protocol, settings and the JSON profile and game stores, release notes, legacy import parsing. **No Win32, no UI.** |
| `src/RigShift.Windows` | `net10.0-windows10.0.26100.0` | OS adapters: CCD API, NVAPI (Surround), Core Audio + `IPolicyConfig`, communications ducking, app launcher, installed-game detection and launching, window placement, USB device list and power check, keep-awake, window rescue, autostart, `rigshift://` registration, shortcuts. CsWin32-generated interop. |
| `src/RigShift.App` | `net10.0-windows10.0.26100.0` | WPF tray app (WPF-UI, H.NotifyIcon), DI host, CLI entry point, single instance + pipe server, display change watcher, hotkeys, automation service, Velopack updates. |
| `tests/RigShift.Core.Tests` | `net10.0` | xunit v3 + Shouldly + NSubstitute. Runs on any OS. |
| `tests/RigShift.Windows.Tests` | `net10.0-windows10.0.26100.0` | CCD struct layout, path building, legacy import, store detection against a fake install folder. |
| `tests/RigShift.App.Tests` | `net10.0-windows10.0.26100.0` | App services without UI automation: switch coordinator, pipe server, settings, automation service, diagnostics report. |

Dependencies point inward only: `App → Windows → Core`. Core defines the interfaces; Windows implements them;
App composes them.

## Core concepts

- **`Profile`** – desired state: displays, audio (endpoints and volume), confirm timeout, hotkey, apps to start or
  stop (optionally after a USB device appears, with a maximum wait), keep awake, communications ducking off.
- **`DisplayAssignment`** – identity, mode (resolution, refresh rate as a fraction), position, rotation, primary,
  optional, custom name, HDR (`null` = leave as is).
- **`DisplayIdentity`** – stable identity (device paths + EDID). Volatile OS handles never leave a snapshot.
- **`DisplaySnapshot`** – what the OS reports right now, including inactive and unavailable targets.
- **`TopologyPlan`** – the result of matching a profile against a snapshot: resolved, missing, warnings,
  `IsBlocked`, `ShouldRetryLater`. Shown to the user instead of raw error codes.
- **`SwitchResult`** – outcome of a switch (`Applied`, `AppliedPartially`, `RolledBack`, `Blocked`, `Failed`, `DryRun`).
- **`GameEntry`** – a game and everything that belongs to a session of it: how it is launched (executable, Steam app
  id, Epic app name), the profile to switch to first, the companion programs, saved window positions, a hotkey, and
  what happens when it ends. Deliberately not a `Profile`: a profile is a state of the machine, a game has a start and
  an end.
- **`GameSessionRunner`** – one session: apply the profile (the same `SwitchOrchestrator`), start the programs before
  the game, put the helper windows back, start the game, learn its process name, wait for it to end, stop the
  programs, run the exit action. RigShift never ends the game itself – unsaved progress, and anti-cheat drivers take a
  dim view of anyone touching their process. What counts as "ended" is a choice (`SessionEnd`): iRacing's interface
  outlives the sim, so hanging the session on the sim would end it between two races.
- **`AutomationRule`** – USB device trigger: switch to a profile once all of the rule's devices are connected, and
  after a delay per rule switch to a profile or back once one of them is gone. `AutomationTrigger` decides from polled
  device ids (pure logic); rules of 1.3 with a single `usbDeviceId` are migrated on load.
- **`UsbDeviceNames`** – custom USB device names by `VID_xxxx&PID_xxxx`, stored in the settings and shown in rules,
  the editor's wait-for-device list, notifications and logs.
- **`SwitchOrchestrator`** – runs one switch: plan, apply with retries, confirm, roll back, HDR, window rescue and
  keep-awake. Audio (`AudioSwitcher`), apps and the wait for their USB device (`AppRunner`) and communications
  ducking (`DuckingSwitcher`) are its internal parts. Apps run after the result: `SwitchResult.AppsCompletion`
  completes with their outcome.

## Switch state machine

```
Idle → Planning → Applying → AudioSwitch → Confirming → Applied
          ↓          ↓                        ↓ timeout
       Blocked     Failed                 RollingBack → RolledBack
                                              ↓ optional displays missing
                                           FollowUp (re-plan on WM_DISPLAYCHANGE while the profile stays active)
```

The hard rules behind every step are in `docs/display-topology.md`
and the decisions in `docs/decisions/`.

## Interfaces (Core → Windows)

| Interface | Windows implementation | Notes |
|---|---|---|
| `IDisplayConfigurator` | `CcdDisplayConfigurator` | `QueryAsync` uses `QDC_ALL_PATHS`; the snapshot is built from the raw CCD paths and modes by the pure function `CcdSnapshotBuilder`, which tests cover without hardware; `ApplyAsync` is one atomic `SetDisplayConfig`. HDR via `DisplayConfigGet/SetDeviceInfo` (24H2 `_2`/`SET_HDR_STATE`, older advanced color as fallback); refresh rates offered via DXGI output mode lists (exact rationals). |
| `IAudioController` | `PolicyConfigAudioController` | Enumerate via `IMMDeviceEnumerator`; default via `IPolicyConfig`; volume via `IAudioEndpointVolume`. |
| `IAppLauncher` | `ProcessAppLauncher` | Starts and stops a profile's programs via `Process`; programs are matched by file name. |
| `IPowerController` | `PowerController` | Keep-awake is a power request (display + system required) that Windows drops when the process ends. |
| `IUsbDeviceList` | `UsbDeviceList` | Present USB devices via `CM_Get_Device_ID_List`, polled every 2 s by `AutomationService` for USB rules and every second by the orchestrator while a profile's apps wait for a device. |
| `IUsbPowerCheck` | `UsbPowerCheck` | Read-only: USB selective suspend of the active scheme on AC (`PowerReadACValueIndex`) and `Device Parameters` flags under `HKLM\…\Enum\USB`. `UsbPowerSaving.ShouldWarn` (Core) decides whether the Automation page warns. |
| `IWindowRescuer` | `WindowRescuer` | After every successful apply (+1 s): `EnumWindows`, visible/uncloaked/non-tool windows that `MonitorFromRect` places on no monitor move to the primary work area via `SetWindowPlacement`. Geometry in `WindowGeometry` (Core). |
| `IDuckingPreference` | `RegistryDuckingPreference` | HKCU `Software\Microsoft\Multimedia\Audio\UserDuckingPreference` (undocumented; 3 = do nothing, missing = reduce by 80 %). The previous value is kept by `IDuckingMemory` (`SettingsDuckingMemory`, App). |
| `IGameLibrary` | `GameLibrary` (Steam + Epic) | Installed games from the stores' own manifests on disk – no sign-in, no token, no account. Steam's `libraryfolders.vdf` and `appmanifest_*.acf` are parsed by `ValveDataFormat`, Epic's by its JSON manifests. |
| `IGameStarter` | `ShellGameStarter` | Starts a store game through `steam://rungameid/` or `com.epicgames.launcher://`, never the executable: overlay, anti-cheat and DRM expect the client in the chain. Only a direct executable hands back the game's own process id. |
| `IGameProcesses` | `SystemGameProcesses` | Read-only listing and waiting. `GameProcessLearner` (Core) works out which new process is the game, filtered by the install folder. |
| `IWindowLayout` | `WindowLayoutManager` | Captures and restores window positions (`SetWindowPlacement`), for the helper windows around a game. |
| `IAutostart` | `RunKeyAutostart` | HKCU `Run`, `--minimized`. |
| `IProfileStore` | `JsonProfileStore` (in **Core**, `Storage/`) | `%AppData%\RigShift\profiles\*.json`, `schemaVersion`. Plain file I/O, so it lives in Core and is tested against a temp directory. Settings: `JsonSettingsStore` (Core). |
| `IGameStore` | `JsonGameStore` (in **Core**, `Storage/`) | `%AppData%\RigShift\games.json`, one file rather than one per game: games are few and are always shown as a list. |
| `ISwitchConfirmation` | `WpfSwitchConfirmation` in `RigShift.App` | Countdown window "Keep these display settings?" on the new primary display; returns `Confirmed`, `Rejected` or `TimedOut`. |

There is no device notification listener. Display changes reach the app through `DisplayChangeWatcher`
(`RigShift.App`, a hidden top-level window receiving `WM_DISPLAYCHANGE`, debounced 1.5 s); it refreshes the active
profile and lets the coordinator catch up on skipped optional displays. USB devices are polled (see `IUsbDeviceList`).

## Process model

- Single instance via named mutex; a second instance forwards its CLI arguments over the named pipe
  `\\.\pipe\RigShift.<SessionId>` (one per Windows session) and exits with the result code. A command that arrives in
  a session without a desktop – over SSH, from a scheduled task – is forwarded to the one listening instance, which
  does sit on the desktop; the display API refuses everything else there. Who may connect is decided by the pipe's
  access list on this user's SID, not by `CurrentUserOnly`, whose owner comparison already fails for an SSH session
  of an administrator.
- CLI: `RigShift.exe apply <name> [--no-confirm] [--dry-run] | toggle | list | save <name> | status | surround |
  games | play <name>`. Exit codes: 0 applied, 1 failed (also: another switch is running), 2 blocked, 3 rolled back,
  4 unknown profile or game, 5 invalid arguments. `play` returns as soon as the session started – it outlives the
  command by hours.
- Logs: `%AppData%\RigShift\logs\rigshift-<date>.log` (Serilog, daily rolling, 14 files).
- Data lives in `%AppData%\RigShift` because Velopack installs into `%LocalAppData%\RigShift` and deletes that
  folder on uninstall.
- Updates: `UpdateService` checks GitHub Releases at startup and every 24 h and downloads a newer version; Velopack
  installs it the next time the tray app starts (never during a CLI call, which would lose its exit code).
- UI: WPF-UI pages scroll through `Controls/WheelScrolling`, which hands the mouse wheel to the page's outer scroll
  viewer; view models live one per file in `RigShift.App/ViewModels`.

## Known limitations (accepted)

Found in the 1.3 analysis and deliberately left as they are:

- Windows calls behind `Task.FromResult` are synchronous; callers that must not block wrap them in `Task.Run` (A-09).
- USB devices are matched by VID/PID only: identical devices count as one, and hubs are filtered by name only (C-07). Polling does some duplicate work per poll; measured, it does not matter (C-08).
- Every save reloads all profiles and rebuilds history and tray menu; the working set grew by 19 MB after 50 saves
  (D-03, watch only).
- `rigshift save` while the profile editor is open: whoever saves last wins, without a warning (F-06).
- Downgrading to an older version drops fields the older version does not know, without a warning (F-07).
- The combo boxes on automation rule cards may not be found by UI Automation; check with `inspect.exe` if screen reader
  support becomes a topic (I-18).

## Coding rules

See `.editorconfig` and `Directory.Build.props`: warnings are errors, analyzers at `latest-recommended`,
nullable enabled, structured logging only (no string interpolation in log calls), `CancellationToken` through
every async chain, never block the UI thread.
