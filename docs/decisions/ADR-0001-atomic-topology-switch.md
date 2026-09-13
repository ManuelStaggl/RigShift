# ADR-0001: Switch display topology atomically via SetDisplayConfig

Status: accepted · Date: 2026-09-13

## Context

NVIDIA GPUs expose a limited number of display heads; DSC displays at high pixel rates consume two. Enabling or
disabling monitors one by one exceeds the budget mid-sequence and fails. Existing tools (MultiMonitorTool,
DisplayMagician, DisplayFusion profiles) do exactly that and therefore fail on this hardware.

## Decision

RigShift always hands the complete target topology (all paths and modes) to a single `SetDisplayConfig` call.
There is no code path that toggles a single display. Displays that are absent are skipped from the path set,
never "disabled" separately.

## Consequences

- Profiles must be complete topologies, not deltas.
- The planner has to resolve every display against the live topology before applying.
- Fallback strategies (database modes, wait for target) operate on the whole set as well.
