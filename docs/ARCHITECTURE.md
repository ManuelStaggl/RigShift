# Architecture

## Projects

| Project | Target | Role |
|---|---|---|
| `src/RigShift.Core` | `net10.0` | Domain: profiles, topology planning, switch orchestration, legacy import parsing. **No Win32, no UI.** |
| `src/RigShift.Windows` | `net10.0-windows10.0.26100.0` | OS adapters: CCD API, Core Audio + `IPolicyConfig`, device notifications, autostart, JSON profile store. CsWin32-generated interop. |
| `src/RigShift.App` | `net10.0-windows10.0.26100.0` | WPF tray app (WPF-UI, H.NotifyIcon), DI host, CLI entry point, single instance, Velopack updates. |
| `tests/RigShift.Core.Tests` | `net10.0` | xunit v3 + Shouldly + NSubstitute. Runs on any OS. |

Dependencies point inward only: `App → Windows → Core`. Core defines the interfaces; Windows implements them;
App composes them.

## Core concepts

- **`Profile`** – desired state: displays (identity + mode + position), audio endpoints, confirm timeout.
- **`DisplayIdentity`** – stable identity (device paths + EDID). Volatile OS handles never leave a snapshot.
- **`DisplaySnapshot`** – what the OS reports right now, including inactive and unavailable targets.
- **`TopologyPlan`** – the result of matching a profile against a snapshot: resolved, missing, warnings,
  `IsBlocked`, `ShouldRetryLater`. Shown to the user instead of raw error codes.
- **`SwitchResult`** – outcome of a switch (`Applied`, `AppliedPartially`, `RolledBack`, `Blocked`, `Failed`, `DryRun`).

## Switch state machine

```
Idle → Planning → Applying → AudioSwitch → Confirming → Applied
          ↓          ↓                        ↓ timeout
       Blocked     Failed                 RollingBack → RolledBack
                                              ↓ optional displays missing
                                           FollowUp (re-plan on WM_DISPLAYCHANGE, ≤ 60 s)
```

Details and the reasoning behind every step live in `docs/PLAN.md` (German) and `docs/display-topology.md`
(English, the hard rules).

## Interfaces (Core → Windows)

| Interface | Windows implementation | Notes |
|---|---|---|
| `IDisplayConfigurator` | `CcdDisplayConfigurator` | `QueryAsync` uses `QDC_ALL_PATHS`; `ApplyAsync` is one atomic `SetDisplayConfig`. HDR via `DisplayConfigGet/SetDeviceInfo` (24H2 `_2`/`SET_HDR_STATE`, older advanced color as fallback); refresh rates offered via DXGI output mode lists (exact rationals). |
| `IAudioController` | `PolicyConfigAudioController` | Enumerate via `IMMDeviceEnumerator`; default via `IPolicyConfig`; volume via `IAudioEndpointVolume`. |
| `IPowerController` | `PowerController` | Keep-awake is a power request (display + system required) that Windows drops when the process ends. |
| `IUsbDeviceList` | `UsbDeviceList` | Present USB devices via `CM_Get_Device_ID_List`, polled every 2 s by `AutomationService` for USB rules and every second by the orchestrator while a profile's apps wait for a device. |
| `IUsbPowerCheck` | `UsbPowerCheck` | Read-only: USB selective suspend of the active scheme on AC (`PowerReadACValueIndex`) and `Device Parameters` flags under `HKLM\…\Enum\USB`. `UsbPowerSaving.ShouldWarn` (Core) decides whether the Automation page warns. |
| `IWindowRescuer` | `WindowRescuer` | After every successful apply (+1 s): `EnumWindows`, visible/uncloaked/non-tool windows that `MonitorFromRect` places on no monitor move to the primary work area via `SetWindowPlacement`. Geometry in `WindowGeometry` (Core). |
| `IDuckingPreference` | `RegistryDuckingPreference` | HKCU `Software\Microsoft\Multimedia\Audio\UserDuckingPreference` (undocumented; 3 = do nothing, missing = reduce by 80 %). |
| `IProfileStore` | `JsonProfileStore` (in **Core**, `Storage/`) | `%AppData%\RigShift\profiles\*.json`, `schemaVersion`. Plain file I/O, so it lives in Core and is tested against a temp directory. |
| `IDeviceEvents` (M2/M4) | `DeviceNotificationListener` | `WM_DISPLAYCHANGE`, `WM_DEVICECHANGE` from a hidden message window. |
| `IAutostart` (M4) | `RunKeyAutostart` | HKCU `Run`, `--minimized`. |
| `ISwitchConfirmation` (M3) | countdown window in `RigShift.App` | "Keep these display settings?" on the new primary display; returns `Confirmed`, `Rejected` or `TimedOut`. |

## Process model

- Single instance via named mutex; a second instance forwards its CLI arguments over the named pipe
  `\\.\pipe\RigShift.<SessionId>` (one per Windows session, current user only) and exits with the result code.
- CLI: `RigShift.exe apply <name> [--no-confirm] [--dry-run] | list | save <name> | status`.
  Exit codes: 0 applied, 1 failed, 2 blocked, 3 rolled back, 4 unknown profile.
- Logs: `%AppData%\RigShift\logs\rigshift-<date>.log` (Serilog, daily rolling, 14 files).
- Data lives in `%AppData%\RigShift` because Velopack installs into `%LocalAppData%\RigShift` and deletes that
  folder on uninstall.
- Updates: `UpdateService` checks GitHub Releases at startup and every 24 h and downloads a newer version; Velopack
  installs it the next time the tray app starts (never during a CLI call, which would lose its exit code).

## Coding rules

See `.editorconfig` and `Directory.Build.props`: warnings are errors, analyzers at `latest-recommended`,
nullable enabled, structured logging only (no string interpolation in log calls), `CancellationToken` through
every async chain, never block the UI thread.
