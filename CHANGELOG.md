# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [3.2.1] - 2026-09-18

### Fixed

- Drop-down lists did not open on a click anywhere in the app since 3.0.0; only the keyboard could change them.

## [3.2.0] - 2026-09-17

### Added

- **A page of its own for the field of view**, in place of the dialog: pick a display, add its curve, your eye
  distance and how the rig stands, and every sim gets the number it asks for.
- Curved panels are calculated as the arc they are: the value a sim renders and what the eye really covers are shown
  side by side. The radius is remembered per display and pre-filled for known models.
- Triple screens take a measured side angle instead of the ideal one, and report the total angle, how far apart the
  outer edges stand, and the pixels Surround hides behind the frames.
- Eighteen sims with their own conventions – degrees, multipliers, sliders, offsets or radians – each with where the
  value goes, how well the convention is backed up, and the sim's own triple-screen fields ready to copy.
- A top view of the rig that glides from one arrangement to the next while you type.
- Pixels per degree, and warnings for arrangements that cannot be built.
- An optional bit of slack on every sim value, for those who want a wider picture than the exact geometry.

### Changed

- The "FOV" button on the overview is gone; the page sits in the navigation between Games and Settings (Ctrl+4).
  Settings and Help moved to Ctrl+5 and Ctrl+6.

## [3.1.0] - 2026-09-17

### Added

- **Field of view from your displays.** The overview has an **FOV** button: pick a display, and its picture size
  comes from the monitor itself, so you only measure the distance from your eyes to the screen. The dialog shows the
  vertical and horizontal angle, for triples the side angle and the combined width, and the number each sim wants
  the way it counts – vertical for Assetto Corsa, ACC, AC EVO, Le Mans Ultimate and RaceRoom, horizontal for
  Automobilista 2 and iRacing, doubled for the F1 games – with a **Copy** button per line. A monitor that reports no
  size (remote sessions, virtual displays) falls back to a diagonal you type. Distance, layout and bezel are
  remembered.

## [3.0.1] - 2026-09-17

### Fixed

- **Text in drop downs sat above the middle of its box** and started a few pixels further left than in a text field.
  Both now sit on the same baseline and the same inset, and a long entry ends in an ellipsis instead of running under
  the arrow.
- **Text in a text field sat too far from the left edge**: the field's padding was counted twice.
- **Cut off text at the window's smallest width.** Two column layouts now put their second column underneath instead
  of running off the page - the display's properties, audio, triggers, the settings groups and the help cards. A
  checkbox or radio button with a long label wraps instead of being clipped.
- **The arrangement's tiles** drop the refresh rate, and then the monitor model, rather than cutting a number in half.
- **The overview's display table** keeps its last column at every window width.
- **The setup assistant and the pickers had a grey title bar and side rail** instead of the brand navy.
- **The app icon inside the app** is drawn as vector now, so it stays sharp in the title bar, the help card and the
  assistant at every scaling.

## [3.0.0] - 2026-09-17

The whole interface was redrawn around what you actually do: pick a profile, look at it, change one thing, switch.
Nothing was taken away and no file changes format - profiles, games, rules and settings carry over untouched.

### Changed

- **One window instead of a list and a dialog.** Profiles and games sit in a list on the left and open right beside it,
  so you see what a profile holds while you edit it. Changes collect in a save bar at the bottom - save or discard,
  nothing is written behind your back, and leaving an edited profile asks first.
- **A profile in five tabs**: displays, audio, apps, triggers, behavior. A game in the five steps of its session:
  game, profile, tools, windows, end. A problem shows up as a dot on the tab it belongs to.
- **Your arrangement as a picture.** Every profile, every list row, the tray and the confirmation show the screens to
  scale, in their real positions, with the main display marked and missing ones hatched. Click a screen to edit it.
- **The USB rules moved onto the profile they switch to**, under **Triggers**, together with that profile's hotkey and
  its desktop shortcut. The separate automation page is gone; pausing all rules is now a switch in the settings and in
  the tray menu.
- **An overview page** opens on what is on right now: the active profile, every display with its mode and status,
  and the switches before this one.
- **Settings in three groups** - switching, app, devices - with every USB device the rules know by name in one table.
  Help is four cards: diagnostics to copy, backup as a ZIP, the links, and what is new.
- **A setup assistant with its steps in view**, so you always see where you are and what is left.
- **The tray popup** now shows each profile with its arrangement, its hotkey and a check mark on the active one, your
  games below it, and during a switch what it is switching to.
- Colors, spacing and type follow the RigShift brand and the Windows accent color; the window frame is painted in the
  brand navy.

### Fixed

- "Unchanged", "Leave" and "Before the game" no longer sit cut off in their fields.

## [2.2.0] - 2026-09-16

### Added

- **Desktop icon positions per profile.** Windows keeps a single icon layout for every arrangement, so switching
  between your desk and your rig leaves the symbols scattered and you sort them by hand. A profile can now carry its
  own layout: arrange the desktop while that profile is up, press **Save positions** in the profile editor, and every
  switch to it puts the symbols back. Symbols that are gone are skipped, new ones keep their place, and a switch you
  do not keep leaves the desktop alone. With "Auto arrange icons" on, Windows decides and RigShift says so.

## [2.1.2] - 2026-09-16

### Fixed

- A monitor that reports a different hardware ID on each of its inputs was no longer found after the cable moved from
  HDMI to DisplayPort: the profile said "display missing" although it was connected and idle. When neither the device
  path nor the EDID finds a display, RigShift now matches it by its name, as long as that name is unique among both
  the profile's displays and the connected ones - two identical monitors stay ambiguous, as before.

## [2.1.1] - 2026-09-16

### Fixed

- Starting a game failed with "the profile could not be applied, the game was not started", and from then on every
  switch was refused as "another switch is running" until RigShift was restarted. A game session applies its profile
  on a background thread, and the tray icon updated itself straight from there instead of on the UI thread.

## [2.1.0] - 2026-09-16

### Added

- **Find installed games** on the games page adds several games in one go: tick what you want from the list of
  what Steam and Epic have installed, and the entries are there. Profile and tools follow afterwards. On an empty
  page it is the one big button - the picker used to sit inside the editor, where nobody found it.
- Games show **their own icon** - in the list, in the picker and on a desktop shortcut. A symbol you pick yourself
  still wins, and where there is neither, a game symbol stands in.

### Changed

- The game editor labels its fields the way the profile editor does: the wait after a program no longer shows a
  bare "0" with nothing saying what it counts.
- The seconds in **Settings → Confirm after switching** say that they are seconds.
- A game without a profile says so under the box ("starts its tools but leaves your screens as they are"), instead
  of only ever explaining the other case.

## [2.0.0] - 2026-09-16

### Added

- **Games.** A game entry brackets a whole evening: switch to its profile, start the wheel software, SimHub and
  Crew Chief in the order you set, put their windows back where they belong, launch the game, and when you quit,
  close the tools again and go wherever you want - stay, back to the profile from before, or on to another one. The
  switch is the one you already know, with its countdown and its rollback; RigShift never ends the game itself.
- **Your installed games are found for you.** The picker lists what Steam and Epic have on this machine, read from
  their own files on disk - no sign-in, no account. A game can also be any executable. Store games start through
  Steam or Epic, so overlay, anti-cheat and DRM see what they expect.
- **The session ends when you are done, not between two races.** For sims whose interface outlives the sim -
  iRacing, Assetto Corsa with Content Manager, rFactor 2, Automobilista 2, DCS - the session hangs on the interface.
  For everything else, on the game itself. Known sims come prefilled.
- **Window positions per game.** Capture where SimHub, Crew Chief and the overlays belong once; every session puts
  them back, on the screens the profile has just set up.
- **Start a game your way:** the Play button, the tray menu, a keyboard shortcut, a `rigshift://play/<name>` link,
  `RigShift.exe play <name>`, or a desktop shortcut that carries the game’s own name and icon.
  `RigShift.exe games` lists what is configured. And if you start the game from Steam yourself, RigShift can
  notice and run the session anyway.

## [1.9.0] - 2026-09-16

### Added

- If RigShift is killed or the machine loses power between changing your screens and the confirmation, the next start
  offers to put the previous layout back. Until now that rollback lived only in the running process.
- **NVIDIA Surround** per profile: leave it as it is, switch it off, or run the grid the profile carries. Surround is
  set before the arrangement, so it goes through the same countdown and the same rollback as everything else - a
  profile you do not keep puts Surround back too. Saving your current setup takes over a running grid. Build the grid
  once in the NVIDIA control panel; RigShift never forces a driver reload, because that closes running games. If the
  driver refuses, the message says what it answered.
- `RigShift.exe surround` reports whether Surround is on and which displays form the grid. It changes nothing.

### Fixed

- Commands now reach RigShift from a Windows session of their own - over SSH, from a scheduled task, from a
  service. Such a session has no desktop, and the display API refuses everything there; the command is handed to
  the RigShift that does sit on the desktop and answered by it. Only your own user may connect, as before.

## [1.8.0] - 2026-09-15

### Changed

- Profile cards show the app names from the app picker ("SimHub") instead of file names ("SimHubWPF").
- Shorter texts on all pages, consistent page headers and section headings, "Test" instead of "Check" for a dry run,
  "Icon" instead of "Symbol", "App" instead of "Program".
- **Reworked look, closer to Windows 11**: RigShift follows your Windows accent color instead of its own blue; settings,
  displays, automation and About use the same card layout as the Windows settings; text buttons lose their icons.
- Profile cards have one button, **Switch**; Test, Edit, Duplicate, default profile, shortcut and Delete are in the
  **…** menu. The apps line shows the programs' own icons.
- Automation rules collapse to one line ("Wheel + Pedals → Sim Rig") and open to edit.
- The profile editor is split into General, Displays, Audio, Behavior and Apps, with labels above the fields.
- Dialogs put the action first: **Save · Cancel**, **Keep · Revert**.
- The setup assistant is shorter and ends with a summary; the tray popup starts directly with the profile list.

### Fixed

- The name field for USB devices suggested "e.g. Left".

## [1.7.0] - 2026-09-15

### Added

- **Back to the previous profile** with one key: **Settings → Shortcut: back to the previous profile** switches to
  the profile that was active before, or to the default profile right after a start. The same target is available as
  `RigShift.exe toggle` and as the `rigshift://toggle` link, e.g. for a single Stream Deck button.
- **Backup:** **About & help → Save backup** writes profiles, automation rules and settings into one ZIP file;
  **Restore backup** puts them back on a new PC or after a reinstall, replacing what is there after a confirmation.

### Changed

- An automation rule no longer switches back while a full-screen game is running: if the wheelbase drops off the USB
  bus mid-race, the end action waits until the game closes. The device coming back meanwhile cancels it as before.

## [1.6.0] - 2026-09-15

### Added

- Setup assistant: on the first start without profiles, RigShift walks you through saving your first setup, switching
  your displays and saving the second one, with the playback device for each. An optional last step creates an
  automation rule: turn your wheelbase (or any USB device) off and on, RigShift picks it and switches between both
  profiles with it. Open it again any time from the **Profiles** page.

### Changed

- The warning that the graphics card may not drive all displays now only appears for NVIDIA cards. Its rule (displays
  with Display Stream Compression take two of four heads) is NVIDIA's; AMD and Intel cards no longer get it until
  their limits are known. Reports from AMD and Intel users are welcome.

## [1.5.0] - 2026-09-14

### Added

- App picker: **Add app** and the folder button in the profile editor open a searchable list of installed programs (Start
  menu) and running apps, with their icons. Programs without a Start menu entry can still be picked as a file.
- When Windows restores a profile's display layout by itself – for example after you switch on a monitor – RigShift
  says so in a notification. A click applies the rest of the profile (audio, apps, keep awake, call ducking) without
  touching the displays.

### Changed

- The **Displays** page and **Identify** number the displays the way Windows counts them (`\\.\DISPLAY1`, `2`, …)
  instead of left to right, so the numbers usually match the Windows display settings.

## [1.4.1] - 2026-09-14

### Changed

- A required display that is not connected (switched off, or logged off from the graphics card in standby) no longer
  blocks the switch at once: RigShift asks you to switch it on and waits up to 30 seconds for it.
  [Monitors in standby](docs/monitor-standby.md) explains how to keep a monitor connected while it sleeps.
- HDR is switched only once the displays have settled after the switch, and RigShift no longer waits forever for a
  graphics driver that does not answer: after 10 seconds the switch goes on without HDR. The profile editor recommends
  trying HDR in the Windows settings first – on a test PC, switching HDR over HDMI froze the graphics driver, also
  without RigShift.
- A switch where only an optional display (for example a spacedesk tablet) is missing counts as switched, not as
  "partially switched"; the notification says the display follows once it is connected.
- The profile editor's "Wait for device" list and the **Automation** page offer every device RigShift knows – from
  rules, profiles and device names – also while it is not connected.
- The **Profiles** page shows which apps a profile starts or closes and which device they wait for.
- The profile editor offers the refresh rates a display reported when it was last on, also while it is off.
- With "Confirm after switching" off, the seconds stay visible (greyed out) in **Settings**, and the profile editor
  explains why "Switch without asking" is greyed out.
- The diagnostic info shows displays by model and a short id instead of full device paths, and error codes only for
  switches that failed.

### Fixed

- Number fields (wait times, confirmation seconds) were too narrow: their buttons covered the number.
- A "not possible" notification named optional displays too; it now names only the displays that blocked the switch.
- Exiting RigShift during the countdown is logged as cancelled instead of timed out.

## [1.4.0] - 2026-09-14

### Added

- USB device names: name a device once on the **Automation** page; the name shows in the rules, in the profile editor's
  "Wait for device" list and in notifications.
- Automation rules with several USB devices: the rule switches once all of them are connected, and its end action runs
  once one of them has been gone for the rule's wait time. Rules from earlier versions keep their device.
- The **Automation** page warns when two rules use the same USB devices – both of them switch when they connect.

### Changed

- **Note when updating:** automation rules no longer have their own on/off switch – pause the automation or delete the
  rule instead. A rule that was switched off in an earlier version is removed when RigShift starts (noted in the log),
  so it cannot suddenly start switching.
- A new automation rule now switches to the default profile (or the first profile) when its device is gone, instead of
  back to the previous profile. "Switch back to the previous profile" is still available for each rule.
- Apps of a profile now start after the switch has finished. While they wait for their USB device, hotkeys, automation
  and other switches work, and a new switch cancels the wait. If something goes wrong with the apps, a separate
  notification says so.
- Apps that wait for a USB device wait a fixed 30 seconds; the field for the longest wait is gone.
- Profile editor: the own confirmation time per profile is replaced by a "Switch without asking" checkbox; the seconds are
  set only in the settings. Profiles with an own time of 0 s switch without asking, all others ask with the settings' time.
- Profile editor: the call devices ("Playback for calls", "Recording for calls") moved into a collapsed "Advanced" section
  and follow playback and recording unless set.
- Display names are edited only on the **Displays** page; the editor still shows name and model.
- "Check" on a profile no longer blocks switching while it runs.
- RigShift is no longer packed into one large EXE: updates download only the files that changed, and the app no longer
  unpacks itself at every start.
- The diagnostic info replaces your user name in paths, and **About & help** and the README say what the log files
  contain before you attach them to an issue.
- `SECURITY.md` describes how updates are delivered and verified, that they are not code-signed, and the remaining
  risk.

### Removed

- The setting "Switch to the default profile when RigShift starts". It could compete with an automation rule at sign-in,
  and Windows restores the last display arrangement after a restart by itself. Existing settings files still load.
- The **Reload** button on the **Profiles** page: profiles are reloaded after every save, also from the command line.
  **Open profile folder** moved next to **Open log folder** on **About & help**, so **Settings** holds only settings.

### Fixed

- The mouse wheel scrolls the pages and the profile editor wherever the pointer is, not only over the scroll bar at the
  right edge, also over text fields.
- **About & help** shows the release notes of a new version compactly under a collapsed **What's new**, instead of
  the whole text with blank lines; the update buttons sit in their own row below the version.
- The result of a switch also shows on the **Profiles** page, not only as a tray notification (Windows hides those
  during full-screen games), and the page says "Switching…" while a switch runs. Clicking the notification of a failed
  or blocked switch opens **About & help**.
- An unexpected error in the window now shows a notification instead of only being logged, so a button no longer seems
  to do nothing.
- Profile editor: Enter saves, and closing with unsaved changes asks before discarding them.
- Deleting a profile asks with a red delete button, centred on the RigShift window.
- Deleting an automation rule asks first, in the same way; "Switch without confirmation" on a rule explains that nothing
  is reverted automatically if a display stays dark.
- If the settings cannot be saved while pausing or resuming the automation, the **Automation** page shows an error and
  the switch returns to the saved state instead of silently showing the wrong one.
- "Refresh devices" on the **Automation** page no longer empties the chosen device of every rule.
- Keyboard shortcuts show key names as your keyboard layout calls them, e.g. "Alt+," or "Ctrl+Alt+Page Up" instead of
  "OemComma" or "Prior".
- The main buttons have access keys (Alt + underlined letter), e.g. Save in the profile editor and Keep/Revert after a
  switch.
- Switching the language in Settings now also updates texts that stayed in the old language until a restart (hints,
  update status, recent switches, the **Displays** page, the **Automation** page's lists and an open profile editor),
  and dates and numbers keep your Windows regional format.
- German texts use one wording throughout ("Wechsel", addressing you directly) and consistent ellipses.
- Layouts checked in German and English, light and dark, narrow and wide: name fields on the **Displays** page no
  longer squeeze the details into a narrow column, and a profile without a symbol no longer leaves a gap.
- After reconnecting to the PC (for example over Remote Desktop), the tray icon and the theme are no longer redrawn
  several times in a row.
- A display that wakes up late and then answers "not ready" gets the full retry time again instead of failing.
- Two identical monitors: if one is unplugged and the other moved to a different port, RigShift no longer guesses which
  one it is; the switch is blocked with a hint.
- A monitor that Windows lists twice (an old and a current entry) is matched to the entry that is ready.
- When Windows had to choose the display modes, RigShift checks the result: a display that stayed dark makes the switch
  count as partial, and the note about Windows' modes only appears when a mode really differs.
- The active profile is up to date as soon as a switch reports its result.
- HDR is set even if a display reports its HDR state only a moment after the switch.
- A program that is not responding no longer holds up a switch while RigShift moves lost windows back; the move
  takes at most 3 seconds and happens only once the switch is confirmed, so a rejected switch leaves windows where
  they were.
- Stopping an app of a profile only ends programs started from the configured folder; a same-named program elsewhere
  keeps running (where Windows lets RigShift read the program's path).
- The USB power-saving warning only looks at the ports the device is connected to right now, also covers devices
  with their own driver (for example Thrustmaster or Logitech G HUB), and logs when the registry cannot be read.
- A desktop shortcut for a profile whose name ends with a backslash now opens the right profile.
- A command-line call or link that connects to RigShift but sends nothing no longer blocks later calls (10-second
  limit).
- Audio devices are released right after use, and HDR changes log which Windows request was used and both result
  codes.

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
