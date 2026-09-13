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
