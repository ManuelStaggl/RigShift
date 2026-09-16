# Desktop icon positions

Windows keeps **one** icon layout for the desktop, not one per display arrangement. Changing the main display or its
resolution makes Explorer lay the icons out again, so switching between a desk and a rig profile scrambles them. The
way back has to be per profile, which is why a profile can carry one.

## Why not the ListView

The desktop icons live in a `SysListView32` under `Progman`, and `LVM_GETITEMPOSITION` / `LVM_SETITEMPOSITION` do reach
them. That stopped being reliable in **Windows 10 1809**: the shell manages the layout itself since then, and positions
poked into the control are ignored or overwritten again. Microsoft says so explicitly and points at the supported API
instead ([The Old New Thing, 2021-11-22][onk]).

Talking to the control also needs memory inside the Explorer process (`VirtualAllocEx`, `ReadProcessMemory`), which is
exactly the kind of access an antivirus flags.

## What RigShift uses

`IFolderView` on the desktop's own shell view, the documented route ([The Old New Thing, 2013-03-18][ont]):

1. `IShellWindows::FindWindowSW` with `SWC_DESKTOP` for the desktop window,
2. its `IServiceProvider` → `SID_STopLevelBrowser` → `IShellBrowser`,
3. `QueryActiveShellView` → `IFolderView`,
4. `ItemCount` / `Item` for the items, `GetItemPosition` per item,
5. `SelectAndPositionItems` with `SVSI_POSITIONITEM` (0x80) to put them back — one call for all of them.

Implementation: `src/RigShift.Windows/Shell/DesktopIcons.cs`.

## Things that bite

- **The shell hands its view to an apartment thread.** Every call runs on RigShift's own STA thread; the switch itself
  runs on a background thread, so the OS layer brings the right thread rather than expecting one.
- **Items are identified by their parsing name**, not by their index: the full path for a file or shortcut, `::{GUID}`
  for the Recycle Bin and the other shell folders. The index changes as soon as anything is added or renamed.
- **"Auto arrange icons" wins.** It overrides every position, so RigShift reads `FWF_AUTOARRANGE` through
  `IFolderView2::GetCurrentFolderFlags` and says so instead of silently doing nothing. "Align icons to grid" is fine —
  it only snaps the restored position to the nearest cell, which is why a restore is compared against what the shell
  made of it, never against what was asked for.
- **Explorer lays the desktop out again a moment after the arrangement changed**, which can undo a restore that came
  too early. The switch therefore checks whether what it placed is still in place and repeats, up to
  `SwitchOptions.DesktopIconAttempts` times.
- **PIDLs from `Item` belong to the caller** and are freed with `ILFree`.
- `IShellBrowser` and COM's `IServiceProvider` are hand-written: CsWin32 refuses `IShellBrowser` for an AnyCPU target
  (`PInvoke005`). Only `QueryActiveShellView` is ever called; the members before it keep the vtable order and must not
  be reordered.

## Checking it by hand

```powershell
dotnet run --project tools/RigShift.Probe -- desktop-icons
dotnet run --project tools/RigShift.Probe -- desktop-icons-restore <file>
```

The first only reads. The second moves the symbols, so capture the current layout first if you want it back. Both work
in an RDP session; neither works over SSH, which has no desktop.

[onk]: https://devblogs.microsoft.com/oldnewthing/20211122-00/?p=105948
[ont]: https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933
