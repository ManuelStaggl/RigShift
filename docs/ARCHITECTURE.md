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
| `IDisplayConfigurator` | `CcdDisplayConfigurator` | `QueryAsync` uses `QDC_ALL_PATHS`; `ApplyAsync` is one atomic `SetDisplayConfig`. |
| `IAudioController` | `PolicyConfigAudioController` | Enumerate via `IMMDeviceEnumerator`; default via `IPolicyConfig`; volume via `IAudioEndpointVolume`. |
| `IProfileStore` | `JsonProfileStore` (M2) | `%LocalAppData%\RigShift\profiles\*.json`, `schemaVersion`. |
| `IDeviceEvents` (M2/M4) | `DeviceNotificationListener` | `WM_DISPLAYCHANGE`, `WM_DEVICECHANGE` from a hidden message window. |
| `IAutostart` (M4) | `RunKeyAutostart` | HKCU `Run`, `--minimized`. |
| `ISwitchConfirmation` (M3) | countdown window in `RigShift.App` | "Keep these display settings?" on the new primary display; returns `Confirmed`, `Rejected` or `TimedOut`. |

## Process model

- Single instance via named mutex; a second instance forwards its CLI arguments over the named pipe
  `\\.\pipe\RigShift` and exits with the result code.
- CLI: `RigShift.exe apply <name> [--no-confirm] [--dry-run] | list | save <name> | status`.
  Exit codes: 0 applied, 1 failed, 2 blocked, 3 rolled back, 4 unknown profile.
- Logs: `%LocalAppData%\RigShift\logs\rigshift-<date>.log` (Serilog, daily rolling, 14 files).

## Coding rules

See `.editorconfig` and `Directory.Build.props`: warnings are errors, analyzers at `latest-recommended`,
nullable enabled, structured logging only (no string interpolation in log calls), `CancellationToken` through
every async chain, never block the UI thread.
