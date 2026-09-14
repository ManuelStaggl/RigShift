# Monitors in standby

RigShift wakes a monitor that sleeps in standby: Windows still lists it, the first attempts fail with "not ready", and
RigShift retries until the monitor answers (usually within a few seconds).

That only works while the monitor stays **connected** in standby. Many monitors log off from the graphics card when
they sleep deeply or are switched off with the power button. Windows then no longer knows the monitor, and no program
can wake it: graphics cards do not send HDMI-CEC, and DDC/CI needs the connection that is gone.

In that case RigShift shows **"Switch on your display"**, waits up to 30 seconds and switches as soon as the monitor
shows up. If it does not, the switch is not done and the notification names the monitor.

## Keep the monitor connected in standby

The names differ between brands and models. Look in the monitor's on-screen menu (OSD), usually under system, power
or eco settings, for options such as **Deep Sleep**, **Standby mode**, **Auto source / input detection**,
**Eco / power saving** or **Auto power off**, and set them so the monitor keeps its input connected while it sleeps
(typically: deep sleep off, automatic input detection off, fixed input). The monitor's manual names the exact options.

Also worth checking:

- **Put the monitor to sleep instead of switching it off** – with the power button many monitors log off, in standby
  they stay connected.
- **Cables and ports:** a monitor on DisplayPort usually stays connected in standby more reliably than on HDMI.
- **Windows:** *Settings → System → Power* – the screen timeout only makes the monitor sleep; it does not log it off.

To see whether your monitor stays connected, put it to sleep, wait a minute and open **Displays** in RigShift: a
monitor that is still listed ("connected, off") can be woken; one that is gone has logged off.

## HDR

Switching HDR on a monitor makes it renegotiate the signal and briefly disconnect. On one test PC, switching HDR on a
monitor connected over HDMI froze the graphics driver – also when HDR was switched in the Windows settings, without
RigShift. Before you set HDR in a profile, switch it once in *Settings → System → Display → HDR* and check that the
monitor comes back.
