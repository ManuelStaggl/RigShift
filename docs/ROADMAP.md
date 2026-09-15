# Roadmap

RigShift is meant to stay small: switch into the rig and back, reliably. Released features are listed in the
[changelog](../CHANGELOG.md).

## Planned

- Head-budget limits for AMD and Intel cards, once community reports show where they are. Today the warning
  ("this card may not drive all of these displays") is NVIDIA-only.
- Fixes and small improvements from user reports. Releases are batched; hotfixes ship on their own.

## Under consideration

Ideas that come up regularly and are not ruled out, but need a real use case before they get built:

- VR headsets as part of a profile (today: audio and apps work, the display side is untested).
- winget distribution.

## Not planned

Reviewed and dropped, because they do not belong to the core or are covered by what exists:

- Game or process triggers with per-game templates (the switch came too late, while the game was already starting).
- Power plan, wallpaper, taskbar or DPI scaling per profile.
- Per-app volume and audio-only profiles.
- Local HTTP API, Home Assistant / MQTT: the CLI, hotkeys and `rigshift://` links cover scripts and Stream Deck.
- DDC/CI monitor input switching, a Stream Deck plugin, confirmation with a wheel button.
- Focus assist, game mode, night light: no official API.
