# RigShift compared

Every tool on this page can switch your screens. If yours works for you, keep it – this page is here so you can
decide in two minutes whether RigShift adds anything for your setup, and where the others are the better pick.

State: September 2026. Facts about other tools come from their own documentation and release pages; corrections are
welcome as an [issue](https://github.com/ManuelStaggl/RigShift/issues).

## What only RigShift does

- **Switches when the wheelbase powers on**, and back when it is off, after a delay you choose. None of the tools
  below has a USB trigger.
- **Reverts on its own when you get no picture.** Every switch can end in a countdown; no key press means the old
  layout comes back – also after a crash in the middle of a switch. Enter, or any button on the wheel, button box or
  controller, keeps it.
- **One profile is the whole PC:** displays, NVIDIA Surround, HDR and refresh rate per monitor, playback and
  microphone (also the separate call devices) with volume, apps and desktop icons.
- **Free and open source (MIT)**, no account, no telemetry, no admin rights, English and German.

## Side by side

✓ yes · ~ partly or by hand · – no · ? not documented

| | RigShift | DisplayMagician 2.7 | DisplayFusion Pro | PitLaunch 1.0 | Monitor Profile Switcher | Batch / NirCmd |
|---|---|---|---|---|---|---|
| Display layout, main display | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| NVIDIA Surround | ✓ | ✓ | ~ macro | – | – | – |
| AMD Eyefinity | – | ✓ | – | – | – | – |
| HDR, refresh rate per monitor | ✓ | ✓ | ~ | refresh rate | – | ~ |
| Audio output and microphone | ✓ with volume | ✓ per shortcut | ~ output | ✓ | – | ~ |
| Start and close apps | ✓ | ✓ | ~ scripts | ✓ | – | ~ |
| Finds installed games | Steam, Epic, EA, Xbox, iRacing | Steam, Epic, GOG, EA, Ubisoft | – | ✓ | – | – |
| Game session, back when it ends | ✓ also when started from Steam | ✓ | ~ | ✓ | – | ~ |
| Window positions of your tools | ✓ per game | – | ✓ | ✓ | – | ~ |
| Desktop icons per profile | ✓ | – | ✓ | ? | – | – |
| **Wheelbase (USB) trigger** | **✓** | – | – | – | – | – |
| **Countdown with automatic revert** | **✓** | – | – | ~ ask first, manual undo | – | – |
| Waits for monitors that are still waking | ✓ | ~ | ? | ~ readiness check | – | ~ retry |
| Hotkeys | ✓ | ✓ | ✓ | ✓ | ✓ | ~ |
| Stream Deck | hotkey, link or shortcut | shortcut | command line | ✓ plugin | batch | ✓ |
| Command line | ✓ with exit codes | ✓ | ✓ | ? | ~ | ✓ |
| Field of view calculator | ✓ 18 sims | ✓ | – | – | – | – |
| Price | free | free | $34 | free in early access | free | free |
| Source | MIT | GPL-3.0 | closed | source viewable | MPL-2.0 | closed / your own |
| Latest stable release | 2026 | Oct 2024 | 2026 | Aug 2026 | May 2021 | – |
| Code-signed | – | – (3.0: planned) | ✓ | ✓ Store edition | – | – |

## Stay with …

**DisplayMagician** if you run AMD Eyefinity, launch games from GOG or Ubisoft, or your setup has worked for years.
It ties a display profile to a game shortcut. RigShift keeps the PC in rig mode as long as the rig is on and adds the
countdown, desktop icons and window positions. DisplayMagician 3.0 is in development and will add audio profiles and
a signed installer.

**DisplayFusion** if you want full window management – taskbars on every screen, window snapping, dozens of
languages, commercial support. Monitor profiles need the Pro licence. RigShift is free, switches Surround as part of
the profile instead of a macro, and has the wheelbase trigger.

**PitLaunch** if you want an install from the Microsoft Store without the SmartScreen warning, a native Stream Deck
plugin and Discord audio settings per setup. It does not switch NVIDIA Surround, has no USB trigger, and paid plans
are announced after early access.

**Monitor Profile Switcher** if all you need is the layout. It is tiny and does exactly that, but has no audio,
Surround, HDR or refresh rate and no update since 2021.

**A batch file, Win+P or a DisplayPort switch** if you have two simple states, one audio output and no tools to start.
Nothing beats zero software. RigShift earns its place in the failure cases: a monitor still asleep, no picture after
the switch, a window left on a screen that is now dark.

**A launcher (SimLauncher, iRacing Companion Launcher)** if you don't need to switch screens. They start your tools
and nothing else – RigShift starts them too, and puts their windows back where they belong.

## Common questions

See the [FAQ](faq.md).
