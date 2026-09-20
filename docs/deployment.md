# Deployment

For people who install RigShift on more than one PC or without clicking through a setup.

## What gets installed where

| What | Where | Notes |
|---|---|---|
| Program | `%LocalAppData%\RigShift` | Per user, no administrator rights. Owned by the installer – it is emptied on install and removed on uninstall, so never keep files there. |
| Profiles, games, settings | `%AppData%\RigShift` | Survives updates and uninstall. |
| Logs | `%AppData%\RigShift\logs` | One file per day, 14 days. "Detailed log" in the settings adds debug lines. |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | Only when switched on in the settings. |
| `rigshift://` links | `HKCU\Software\Classes\rigshift` | Written on install, removed on uninstall. |

Nothing is written outside the user profile, nothing under `HKLM`.

## Silent install

```powershell
RigShift-win-Setup.exe --silent
```

`--silent` shows no dialog and does not start RigShift afterwards. `--installto <dir>` picks another folder,
`--log <file>` writes an installer log. Setup runs per user: deploy it in the user's context, not as SYSTEM.

Check the download first:

```powershell
gh attestation verify RigShift-win-Setup.exe --repo ManuelStaggl/RigShift
```

or compare it with `SHA256SUMS` from the same release.

## Silent uninstall

```powershell
& "$env:LocalAppData\RigShift\Update.exe" uninstall --silent
```

Removes the program folder, the shortcuts, the autostart entry and the link handler. `%AppData%\RigShift` stays;
delete it for a clean slate.

## Without internet

Use `RigShift-win-Portable.zip`: unpack anywhere, start `RigShift.exe`. Without a connection the update check fails
quietly (one log line), everything else works – RigShift needs the network for nothing but updates.
To update an offline PC, install the newer `Setup.exe` over the old version; profiles and settings stay.

## Moving a setup to another PC

Help → Backup → "Save backup" writes profiles, games and settings into one ZIP; "Restore backup" on the other PC reads
it back and lists what it will change first. Profiles find displays by their EDID, so the same monitors on another
PC are recognised. Audio devices are stored by their Windows endpoint ID, which differs per PC – pick them again in
each profile after the move.

## Updates

See [SECURITY.md](../SECURITY.md#updates-and-their-residual-risk) for where updates come from and how to verify them,
and [PRIVACY.md](../PRIVACY.md) for what the check transmits. "Only notify about updates" in the settings keeps a PC on
its version until someone installs the update by hand.

## Command line

`RigShift.exe list | status | apply <profile> [--dry-run] | toggle | games | play <game>` talks to the running tray
app and returns exit code 0 on success – usable from scripts, Task Scheduler or a Stream Deck.
