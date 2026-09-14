# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- The automation page warns when two rules use the same USB device – both of them switch when it connects.

### Changed

- RigShift is no longer packed into one large EXE: updates download only the files that changed, and the app no longer
  unpacks itself at every start.
- Profile editor: the own confirmation time per profile is replaced by a "Switch without asking" checkbox; the seconds are
  set only in the settings. Profiles with an own time of 0 s switch without asking, all others ask with the settings' time.
- Profile editor: the call devices ("Playback for calls", "Recording for calls") moved into a collapsed "Advanced" section
  and follow playback and recording unless set.
- Apps that wait for a USB device wait a fixed 30 seconds; the field for the longest wait is gone.
- Display names are edited only on the Displays page; the editor still shows name and model.

### Fixed

- Profile editor: Enter saves, and closing with unsaved changes asks before discarding them.
- Deleting a profile asks with a red delete button, centred on the RigShift window.
- Deleting an automation rule asks first, in the same way; "Switch without confirmation" on a rule explains that nothing
  is reverted automatically if a display stays dark.
- Changing the language also updates the automation page's lists and an open profile editor without a restart.
- "Refresh devices" on an automation rule no longer empties the chosen device of every rule.
- The main buttons have access keys (Alt + underlined letter), e.g. Save in the profile editor and Keep/Revert after a
  switch.
- Automation rules no longer have their own on/off switch – pause the automation or delete the rule instead. A rule that
  was switched off in an earlier version is removed when RigShift starts (noted in the log), so it cannot suddenly start
  switching.
- A new automation rule now switches to the default profile (or the first profile) when its device is gone, instead of
  back to the previous profile. "Switch back to the previous profile" is still available for each rule.

### Fixed

- If the settings cannot be saved while pausing or resuming the automation, the automation page shows an error and the
  switch returns to the saved state instead of silently showing the wrong one.
### Changed

- Apps of a profile now start after the switch has finished. While they wait for their USB device, hotkeys, automation
  and other switches work, and a new switch cancels the wait. If something goes wrong with the apps, a separate
  notification says so.
- "Check" on a profile no longer blocks switching while it runs.

### Fixed

- A display that wakes up late and then answers "not ready" gets the full retry time again instead of failing.
- Two identical monitors: if one is unplugged and the other moved to a different port, RigShift no longer guesses which
  one it is; the switch is blocked with a hint.
- A monitor that Windows lists twice (an old and a current entry) is matched to the entry that is ready.
- When Windows had to choose the display modes, RigShift checks the result: a display that stayed dark makes the switch
  count as partial, and the note about Windows' modes only appears when a mode really differs.
- The active profile is up to date as soon as a switch reports its result.
### Fixed

- Stopping an app of a profile only ends programs started from the configured folder; a same-named program elsewhere
  keeps running (where Windows lets RigShift read the program's path).
- A desktop shortcut for a profile whose name ends with a backslash now opens the right profile.
- A program that is not responding no longer holds up a switch while RigShift moves lost windows back; the move
  takes at most 3 seconds and happens only once the switch is confirmed, so a rejected switch leaves windows where
  they were.
- HDR is set even if a display reports its HDR state only a moment after the switch.
- The USB power-saving warning only looks at the ports the device is connected to right now, also covers devices
  with their own driver (for example Thrustmaster or Logitech G HUB), and logs when the registry cannot be read.
- A command-line call or link that connects to RigShift but sends nothing no longer blocks later calls (10-second
  limit).
- Audio devices are released right after use, and HDR changes log which Windows request was used and both result
  codes.

### Changed

- The diagnostic info replaces your user name in paths, and **About & help** and the README say what the log files
  contain before you attach them to an issue.
- `SECURITY.md` describes how updates are delivered and verified, that they are not code-signed, and the remaining
  risk.
### Removed

- The setting "Switch to the default profile when RigShift starts". It could compete with an automation rule at sign-in,
  and Windows restores the last display arrangement after a restart by itself. Existing settings files still load.
- The **Reload** button on the Profiles page: profiles are reloaded after every save, also from the command line.
  **Open profile folder** moved next to **Open log folder** on **About & help**, so Settings holds only settings.

### Fixed

- The mouse wheel scrolls the pages and the profile editor wherever the pointer is, not only over the scroll bar at the
  right edge, also over text fields.
- **About & help** shows the release notes of a new version compactly under a collapsed **What's new**, instead of
  the whole text with blank lines; the update buttons sit in their own row below the version.
- The result of a switch also shows on the Profiles page, not only as a tray notification (Windows hides those during
  full-screen games), and the page says "Switching…" while a switch runs. Clicking the notification of a failed or
  blocked switch opens **About & help**.
- An unexpected error in the window now shows a notification instead of only being logged, so a button no longer seems
  to do nothing.
- Keyboard shortcuts show key names as your keyboard layout calls them, e.g. "Alt+," or "Ctrl+Alt+Page Up" instead of
  "OemComma" or "Prior".
- Switching the language in Settings now also updates texts that stayed in the old language until a restart (hints,
  update status, recent switches, the Displays page), and dates and numbers keep your Windows regional format.
- German texts use one wording throughout ("Wechsel", addressing you directly) and consistent ellipses.

## [1.3.1] - 2026-09-14

### Added

- **About & help** has a "Buy me a coffee" link to Ko-fi for anyone who wants to support RigShift – entirely optional.

### Fixed

- The Windows setting for sounds during calls comes back even if RigShift was closed, crashed or the PC restarted while a
  profile with "Don't lower game sound during calls" was active. RigShift now remembers the previous value in its settings and
  restores it at the next start or with the next profile.
- Quitting RigShift or signing out of Windows during the confirmation countdown no longer leaves displays and audio
  half-switched: the switch is rolled back first (at most 30 s), then RigShift exits.
- Automation: if the switch after connecting a device does not happen (another switch is running or a display is not
  ready yet), the rule tries again after its wait time while the device stays connected. If you reject the switch or it
  fails, turning the device off and on again starts it again right away – and the rule no longer switches back later.
- Automation: a device that disappears for a single moment no longer counts as gone, even with a wait time of 0 s, and
  waking the PC from sleep no longer ends a wait time that started before sleep.
- After a failed switch the notification says whether the previous displays are back or could not be restored.
- If catching up with a display that connected later (e.g. spacedesk) fails, the previous displays are restored like
  after any failed switch, and an unexpected error from Windows during a switch no longer skips that restore.
- A display that answers "not ready" and then "invalid" while waking up is waited for instead of failing at once.
- The status message on the Profiles page shows up again after you closed it once.
- Command line, shortcuts and `rigshift://` links also work for a second Windows user signed in at the same time (the
  command pipe is now per session), and command pipe errors are logged instead of failing silently.
- The Profiles page, the automation rules and the profile editor stay usable in a narrow window: titles, names and
  buttons wrap instead of overlapping or being cut off. Long profile names end with "…"; names can be 60 characters at
  most.
- A profile file that is locked by another program (e.g. an editor or a virus scanner) no longer goes missing silently:
  RigShift reads it even while another program has it open, tries again after a moment, and the Profiles page names
  every file it could not read. `rigshift save` refuses to create a new profile while a file is unreadable, so it
  cannot create a second profile with the same name.

### Security

- Links always ask for confirmation now: a `rigshift://apply/…` link no longer switches without asking when "Confirm
  after switching" is off or the profile's timeout is 0 (it waits at least 15 s for your answer).

### Changed

- Uninstalling RigShift removes its autostart entry, so Windows no longer tries to start a deleted program at sign-in.
  Your profiles and settings in `%AppData%\RigShift` stay.
- The log now records every automation decision (device connected, gone, back, end action skipped and why).
- The log records how long a switch and each of its steps took (display attempts, audio, HDR, apps, moving windows)
  and when a switch was confirmed. Update installation steps are logged too, a day's log file starts anew at 50 MB,
  and the startup no longer writes framework lines.
- The profile editor opens wider, so refresh rate, HDR and the display options fit on one line.

## [1.3.0] - 2026-09-14

### Added

- Volume per profile for the playback and the recording device. If you do not confirm the switch, the previous
  volume comes back too.
- Apps per profile: start or stop programs (for example SimHub or Crew Chief) in a fixed order once the switch is
  confirmed, with an optional wait after each one. Programs that already run are not started twice.
- Your own names for monitors, e.g. "Left", shown as "Left · CM27X3" everywhere. Name a monitor once in the profile
  editor and every profile with it uses the name.
- New page **Displays**: all connected monitors with their state and profiles, a name field for each, and
  **Identify**, which shows a large number on every active screen.
- New page **About & help**: version and updates, recent switches, **Copy diagnostic info** for bug reports, the log
  folder and links to GitHub.
- New page **Automation**: switch profiles when a USB device connects, e.g. to the rig when you turn on the wheelbase
  and back when you turn it off. Pick the device from the connected ones; it keeps matching in another USB port. Each
  rule decides what happens when the device is gone (stay, switch back or switch to another profile), how long it
  must stay gone first (10 s by default, so a quick power cycle changes nothing) and whether to ask for confirmation.
  Pause all rules from the page or the tray menu.
- Keep the PC awake per profile: no sleep, screen saver or display timeout while racing with only a wheel. If you do
  not confirm the switch, it is undone.
- HDR per display: on, off or unchanged. Saving the current arrangement remembers it; if you do not confirm the
  switch, HDR goes back too.
- Choose the refresh rate per display in the profile editor from the rates the monitor offers.
- Lost windows come back: after a switch, windows left on a screen that is now off (Discord, Steam, SimHub …) move
  to the main screen, keeping their size and minimized or maximized state.
- Apps can wait for a device: pick a USB device (e.g. the wheelbase) in the profile's apps section, and the apps start
  once it is connected, after 30 s at most. If it does not show up, the apps start anyway and RigShift tells you.
- USB power-saving check: the Automation page warns when Windows may turn off a rule's device to save power (pedals or
  a wheelbase dropping out) and links to a [step-by-step guide](docs/usb-power-saving.md). RigShift never changes
  these settings itself.
- Per profile, turn off the Windows setting that lowers other sounds during calls, so the game stays loud while you
  talk on Discord. A profile without it brings your previous setting back.

### Changed

- The Diagnostics page is gone; its useful parts moved to **About & help**. Updates are installed from there too.

### Fixed

- Long display positions in the profile editor are no longer cut off.
- A hand-written profile without rotation, audio section or device names now loads with sensible defaults.

## [1.2.0] - 2026-09-14

### Added

- Keyboard shortcut per profile (for example Ctrl+Alt+F1). It switches from anywhere while RigShift runs, with the
  usual confirmation; pressing it again during the countdown keeps the new settings. Set it in the profile editor.
  If another app already uses the shortcut, RigShift tells you when it starts and when you save the profile.
- Links like `rigshift://apply/Rig` switch to a profile, e.g. from a browser bookmark, Win+R or a Stream Deck
  "Website" action. They switch with the usual confirmation (none if it is turned off) and cannot change profiles. Available in the installed version.
- New switch "Confirm after switching" in the settings, instead of entering 0 seconds.

### Fixed

- A settings file without a confirmation time no longer turns the confirmation off.

## [1.1.0] - 2026-09-14

### Added

- Settings show the installed version, the update status and a "Check for updates" button.
- A downloaded update can be installed right away from the settings or the tray menu; clicking the update
  notification opens the settings.
- New setting "Install updates automatically". When it is off, RigShift only reports a new version and downloads and
  installs it when you choose to.
- The settings show what is new in an available update, with a link to the release on GitHub.

## [1.0.1] - 2026-09-13

### Fixed

- `RigShift.exe list` lists the profiles even when the display configuration cannot be read (for example over
  SSH); the active profile is then not marked.

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
