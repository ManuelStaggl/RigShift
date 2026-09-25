# Display topology: hard-won rules

These facts were learned on real hardware (NVIDIA RTX 4080 SUPER, Windows 11, 4K@165 + 2×1080p@100 on the desk,
5120×1440@240 over HDMI + a spacedesk virtual display in the rig). They are the reason RigShift exists.
Do not relearn them; do not refactor them away.

## 1. Switch the whole topology in ONE call

NVIDIA GPUs have a fixed number of internal display heads (four on current GeForce cards). Displays that need
Display Stream Compression at high pixel rates – 4K@165 Hz, 5120×1440@240 Hz – consume **two** heads each
(NVIDIA KB 5338 / 5788). Consequently a 49" ultrawide at 240 Hz cannot be active together with a 4K@165 monitor
plus two more monitors.

The planner's head-budget warning applies this rule to NVIDIA adapters (and adapters without a PCI vendor id). AMD
(`VEN_1002`) and Intel (`VEN_8086`) cards are not checked: their limits are not verified, and a false warning is worse
than none. The single-call rule below applies to every vendor.

Tools that enable and disable monitors one at a time (NirSoft MultiMonitorTool, most "profile" tools) hit the
limit mid-sequence and fail. The only reliable approach is to hand the complete target topology to
`SetDisplayConfig` in a single call:

```
flags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES
```

Both directions (desk → rig, rig → desk) were verified with return code 0.

## 2. Never persist adapter LUIDs or target IDs

`DISPLAYCONFIG_PATH_INFO.sourceInfo.adapterId`, `targetInfo.adapterId` and `targetInfo.id` change after reboots,
driver reinstalls and reconnects (virtual adapters such as spacedesk get a new LUID every time). Profiles store:

- the adapter device path (`DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME`),
- the target device path and EDID IDs (`..._GET_TARGET_NAME`).

At switch time, query **all** paths (`QDC_ALL_PATHS`), build `adapterPath → LUID` and
`adapterPath + targetPath → targetId` maps, and rewrite the stored structs. Mode entries must be rewritten too
(adapter LUID, and target ID for target modes) and re-indexed, because `modeInfoIdx` points into the array you
pass in.

## 3. Missing displays are skipped, not fatal

A spacedesk display exists only while the viewer is connected. A profile that lists it must still apply with the
remaining displays. Mark such displays optional; skip them when absent; re-apply the full path set when they
appear (`WM_DISPLAYCHANGE`).

## 4. Error 31 (`ERROR_GEN_FAILURE`) means "not ready", not "impossible"

Observed 2026-09-13 13:32: the first rig switch failed with 31 for both the "with modes" and the "database modes"
attempt. Twenty seconds later the same call succeeded (database modes). Most likely cause: the HDMI-connected
ultrawide had not woken up yet (`targetAvailable == 0`).

Strategy: after a failure, re-query the topology and wait for the target to report available (poll, ~1 s) within
a time budget, then retry. A fixed sleep is a workaround, not a solution.

Observed 2026-09-13 19:52 (G9 in standby): the sleeping ultrawide still reports `targetAvailable`, the first call
returns 31, and while it wakes it **drops off the bus entirely** for about three seconds – the next call returns
**1610** (`ERROR_BAD_CONFIGURATION`), and the desk screens go dark until Windows reverts. Treat 1610 like 31, and
after a failed attempt keep waiting for a required display that vanished instead of giving up. If a switch still
fails, re-apply the previous topology when displays were left dark.

## 5. Fallback: apply without modes

If `SetDisplayConfig` with the stored modes fails, set every `modeInfoIdx` to `DISPLAYCONFIG_PATH_MODE_IDX_INVALID`,
pass zero modes, and let Windows pick modes from its own database. This succeeded where the stored modes failed.

## 6. Audio default device: `IPolicyConfig`

There is no documented API to set the default audio endpoint. The COM interface `IPolicyConfig`
(CLSID `870af99c-171d-4f9e-af0d-e63df40c2bc9`, IID `f8679f50-850a-41cf-9c72-430f290290c8`) works on Windows 10/11
and needs no elevation. Set all three roles (console, multimedia, communications) unless a profile assigns
communications separately. Check `DEVICE_STATE_ACTIVE` first; setting an unplugged device as default fails.

## 7. Things that broke the setup

- **LittleBigMouse** (mouse routing between screens) prevented the cursor from reaching the spacedesk screen.
- **DisplayMagician** and **MultiMonitorTool** could not perform the atomic switch (see rule 1).
- Surface Pro as a wired monitor is impossible (USB-C is output-only); spacedesk over Wi-Fi is the workable path,
  Miracast the native alternative.

## 8. Windows switches on its own when the set of monitors changes

Observed 2026-09-13 20:17 (M5): `SDC_SAVE_TO_DATABASE` stores each layout under the set of connected monitors. When a
display appears or disappears (a spacedesk viewer connects, a sleeping monitor drops off), Windows re-applies whatever
layout its database holds for the new set – here it jumped from the rig to the desk when spacedesk connected, and
back to the rig when it disconnected. The active profile after a display change therefore says nothing about the
user's intent. The follow-up pass re-applies the pending profile whenever one of its missing displays resolves,
regardless of which profile Windows made active; it only gives up when another profile is active and no missing
display showed up.

## 9. Reference implementation

`legacy/DisplayProfile.ps1` contains the proven C# interop for save/apply (structs, mapping, fallback, retry)
and the `IPolicyConfig` declaration. RigShift reimplements it with CsWin32-generated types, but the algorithm
in `Apply()` is the contract.

## 10. Telling identical monitors apart

Read from the registry of the reference PC on 2026-09-24
(`HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<model>\<instance>\Device Parameters\EDID`):

- A monitor path is `\\?\DISPLAY#<model>#<card part>&0&UID<port>#{…}`. The card part is derived from the graphics
  card's own device instance, so another card, another slot or a BIOS update changes **every** path at once. The
  `UID` part numbers the card's ports (4352, 4353, … on the RTX 4080 SUPER) and stays when only the card's identity
  changed.
- Only the EDID serial number tells identical monitors apart. The two CM27X3 report different small numbers in bytes
  12–15 and no serial text; the XG32UCWG reports the filler `0x01010101` there and its real serial number as text
  (descriptor tag `0xFF`); the Odyssey G93SC reports the same serial number over HDMI and DisplayPort although its
  model code differs. RigShift stores a hash of both fields and compares it only within the same model.
- The registry keeps an instance for every port a monitor was ever seen on. Read the EDID of the live monitor path's
  instance, nothing else.
- Twins that neither serial number nor port tell apart are reported as such – never guessed, never waited for:
  switching a display on does not help there.
