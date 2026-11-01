# ADR-0003: Confirm-or-rollback safety net

Status: accepted · Date: 2026-09-13

## Context

A wrong topology can leave the user without any picture. Windows itself asks "Keep these display settings?"
after a change; a profile switcher needs the same protection, but it must not be fooled by input that does not
prove a visible screen.

## Decision

After applying a profile, RigShift shows a countdown dialog on the new primary display. The switch is kept only
on an explicit action (click, Enter, or the confirm hotkey). Mouse movement does **not** count. On timeout the
snapshot taken before the switch is re-applied with the same retry logic. `Esc` triggers rollback immediately.

Profiles can set the timeout to 0 to disable the net; CLI callers can pass `--no-confirm`.

## Consequences

- The orchestrator must capture a full before-snapshot, including modes, before every apply.
- Rollback failures are reported as `Failed` with a log pointer; there is no third fallback.
