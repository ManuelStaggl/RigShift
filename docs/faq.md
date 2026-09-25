# FAQ

## Installing

**Windows says "Windows protected your PC".**
RigShift is a free hobby project and not code-signed. SmartScreen warns about every unsigned file until enough people
have run it, and each new version starts from zero. Click **More info → Run anyway**. How to check the file yourself:
[Is it safe?](../README.md#is-it-safe)

**Windows says "Smart App Control blocked an app".**
Smart App Control (Windows 11) blocks unsigned apps without a "run anyway" option. RigShift can't run while it is on.
You can turn it off under Windows Security → App & browser control; on Windows 11 25H2 and later it can be turned on
again afterwards.

**My antivirus reports a threat.**
Check the VirusTotal link in the release. Generic machine-learning detections (names ending in `!ml`) hit the small
installer stub that RigShift shares with other apps built on [Velopack](https://github.com/velopack/velopack), not
RigShift itself. If your scanner flags a file, please open an issue with the detection name.

**Why is the download 80 MB?**
RigShift brings its own .NET runtime, so there is nothing to install first and nothing that breaks when another app
updates .NET. Updates after that are usually a few MB.

**Does it need admin rights?**
No. It installs for your Windows user into `%LocalAppData%\RigShift`, and everything it changes – displays, audio,
apps – is what your user may change anyway.

**Does it talk to the internet?**
Only to ask GitHub for new releases. No account, no telemetry, no analytics. Details: [privacy](../PRIVACY.md).

**How do I uninstall it?**
Windows Settings → Apps → RigShift → Uninstall. That removes the program and the desktop shortcuts RigShift created.
Your profiles stay in `%AppData%\RigShift`; delete that folder too if you want everything gone.

## Hardware

**Does it work with AMD or Intel graphics?**
It should: the display switching uses the plain Windows display API, which every driver supports. I only have NVIDIA
to test on, so a [hardware report](https://github.com/ManuelStaggl/RigShift/issues/new?template=hardware_report.yml)
from your setup helps everyone. AMD Eyefinity is not supported.

**Does it switch NVIDIA Surround?**
Yes, per profile: a profile can turn Surround on with your bezel correction, or off. I don't run Surround myself, so the
full on/off cycle hasn't been tested on a real Surround rig yet – reports are welcome.

**Triple screens?**
Yes, with or without Surround. The field-of-view page knows triple-screen fields for the sims that ask for them.

**VR?**
Audio and apps work in a VR profile, and a USB rule reacts to any USB device, a headset's link box included. The
headset itself is not part of the display layout, and I haven't tested a VR setup.

**A monitor is connected through spacedesk, a KVM or a DisplayPort switch.**
Mark it as optional in the profile: RigShift then switches even when it is missing. RigShift can only switch displays
Windows sees at that moment, so a monitor behind a switch that points elsewhere counts as missing.

**My G9 (or another monitor) takes long to wake up.**
RigShift wakes sleeping monitors and waits for them before it switches. More in
[monitors in standby](monitor-standby.md).

## Switching

**What happens if I get no picture after a switch?**
With "Confirm after switching" on, a countdown appears. If nobody presses Enter or a button on the wheel, button box
or controller, the old layout comes back by itself. If the PC crashes in the middle of a switch, RigShift restores the
previous layout at the next start. **Ctrl+Alt+Shift+D** turns every display on, whatever happened.

**Can the wheelbase switch the PC?**
Yes. A USB rule on the rig profile switches when the wheelbase (or pedals, or both) connects, and back to your desk
after it has been gone for the time you set. If the wheelbase drops off USB now and then, see
[USB power saving](usb-power-saving.md).

**I already use DisplayMagician / DisplayFusion.**
Keep it if it works. What RigShift does differently: [comparison](compare.md).

**Can I control it from a Stream Deck or a script?**
Yes: hotkeys, desktop shortcuts, `rigshift://` links and a command line with exit codes. See the
[README](../README.md#stream-deck-and-button-boxes).
