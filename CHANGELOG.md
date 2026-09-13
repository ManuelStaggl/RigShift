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
