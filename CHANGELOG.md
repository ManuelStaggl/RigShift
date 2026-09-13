# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-09-13

First release.

### Added

- **Atomic switching:** a profile's complete display topology is applied in a single `SetDisplayConfig` call, so
  NVIDIA display-head limits are not exceeded mid-sequence. Displays are matched by device path with an unambiguous
  EDID fallback; the estimated head budget is checked before switching.
- **Robust on real hardware:** waits for displays that are still waking up (including monitors that briefly drop off
  the bus, Windows errors 31 and 1610), treats displays as required or optional, picks up optional displays such as
  spacedesk as soon as they connect, and restores the previous arrangement if displays stay dark.
- **Safety net:** a keep-or-revert countdown on the new primary display (Esc reverts from any screen); without
  confirmation display and audio return to the previous state. The confirmation time can be set per profile, 0 skips it.
- **Audio:** default playback and recording devices per role for every profile.
- **Tray app:** tray icon showing the active profile's symbol in the taskbar's color, profile popup, main window with
  profiles, diagnostics and settings (default profile, apply at startup, start with Windows, confirmation time,
  language), balloon notifications, English and German UI, Windows light/dark/high-contrast theme.
- **Profile management:** save the current arrangement, editor (name, symbol, confirmation time, main and optional
  displays, audio devices), duplicate, delete, desktop shortcut per profile, import of the legacy PowerShell script's
  profiles.
- **Command line:** `RigShift.exe apply <name> [--no-confirm] [--dry-run]`, `list`, `save <name>`, `status` with
  documented exit codes; commands are forwarded to the running tray instance.
- **Installer and automatic updates** (Velopack): per-user setup without admin rights, portable ZIP, update check at
  startup and every 24 hours, installation on the next start.
