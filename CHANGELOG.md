# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Project skeleton (milestone M0): solution, Core/Windows/App projects, domain model, OS interfaces,
  legacy `.display` parser with tests, CI workflow, documentation and roadmap.
- Switch logic (milestone M1): `TopologyPlanner` matches profile displays by device path with an unambiguous
  EDID fallback, reports missing/sleeping displays and warns about the estimated display-head budget;
  `SwitchOrchestrator` waits for sleeping targets, retries with database modes and on error 31 within a time
  budget, switches audio without failing the display switch, and rolls back display and audio when the user
  does not confirm (`ISwitchConfirmation`).
- Windows layer (milestone M2): `CcdDisplayConfigurator` (topology snapshot over all paths, one atomic
  `SetDisplayConfig` with per-display source assignment), `PolicyConfigAudioController` (Core Audio enumeration,
  default endpoint via `IPolicyConfig`, volume), `JsonProfileStore` with schema version and atomic writes,
  import of the legacy script's `.display` files and `DisplayProfiles.json`, and the read-only
  `tools/RigShift.Probe` check tool.
- Tray app (milestone M3): tray icon reflecting the active profile, profile popup and context menu, main window
  with profiles (switch, dry-run check), diagnostics (displays, audio devices, recent switches) and settings
  (default profile, apply at startup, start with Windows, confirmation time, language), keep-or-revert countdown
  window on the new primary display with a global Esc hotkey, balloon notifications, English and German UI,
  Windows light/dark/high-contrast theme, detection of the active profile after every display change.
- Command line and profile management (milestone M4): `RigShift.exe apply <name> [--no-confirm] [--dry-run]`,
  `list`, `save <name>` and `status` with documented exit codes; a single tray instance per session that receives
  commands from further processes over the `\\.\pipe\RigShift` named pipe (starting the tray app when needed);
  "save current arrangement" in the window and tray menu, profile editor (name, tray icon, own confirmation time,
  main and optional displays, playback/recording devices per role, take over the current arrangement), duplicate,
  delete and desktop shortcuts per profile.
- Brand identity (milestone M4.5): new RigShift app icon for the executable and all windows; brand blue as accent
  color (the app still follows the Windows light, dark and high-contrast theme); five Fluent profile symbols (desk,
  sim rig, VR, TV/couch, streaming) selectable in the editor and shown in the profile list and tray popup, filled for
  the active profile; the tray icon
  shows the active profile's symbol in the taskbar's color at the current DPI and the RigShift symbol while
  switching or when no profile is recognized; tray popup redesigned with the active profile marked by highlight,
  check mark and "Active"; logo in the README.
