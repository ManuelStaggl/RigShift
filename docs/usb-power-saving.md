# USB power saving and sim hardware

Windows can cut the power of a USB device it considers idle. For a keyboard that is harmless; for pedals, a
wheelbase or a button box it means inputs drop out in the middle of a race, or the device disconnects and reconnects
(which can also trigger or end a RigShift automation rule).

RigShift checks this for the devices of your USB rules and shows a warning on the **Automation** page when Windows is
allowed to power one of them down. RigShift only reads these settings – it never changes them. Both steps below are
Windows settings you change yourself.

## 1. Turn off "USB selective suspend" in the power plan

1. Press **Win+R**, type `control powercfg.cpl` and press Enter.
2. Next to the active power plan, click **Change plan settings**, then **Change advanced power settings**.
3. Expand **USB settings** → **USB selective suspend setting**.
4. Set **Plugged in** (and **On battery** on a laptop) to **Disabled**, then click **OK**.

On some Windows 11 builds the USB settings are hidden in this dialog. In that case, open a terminal as administrator
and run:

```
powercfg /SETACVALUEINDEX SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0
powercfg /SETACTIVE SCHEME_CURRENT
```

The setting belongs to the power plan: if you switch plans, check it again there. RigShift only checks the
**Plugged in** value; on a laptop running on battery, check **On battery** yourself.

## 2. Don't let Windows turn off the device

1. Right-click the Start button and open **Device Manager**.
2. Expand **Human Interface Devices** (pedals, wheelbases and button boxes usually show up here as
   "USB Input Device" or "HID-compliant game controller") and **Universal Serial Bus controllers**.
3. Open the device, go to the **Power Management** tab and clear
   **Allow the computer to turn off this device to save power**. Click **OK**.
4. Do the same for the **USB Root Hub** and **Generic USB Hub** entries the device is connected to.

Not every device has a Power Management tab; then there is nothing to change for it. Devices with their own driver
(for example Thrustmaster or Logitech G HUB) store the checkbox in a different place in the registry; RigShift reads
both places.

For a device without a serial number the setting is stored per USB port, so check it again after plugging the device
into another port; a device with a serial number keeps its setting on every port. RigShift only looks at the ports
the device is connected to right now.

## Checking the result

Open the **Automation** page in RigShift and click the refresh button next to the device list. The warning disappears
once Windows no longer may power the device down. A reconnect of the device (or a restart) may be needed before
Windows reports the new state.
