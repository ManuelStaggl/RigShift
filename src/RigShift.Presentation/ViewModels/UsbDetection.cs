using System.ComponentModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;

namespace RigShift.App.ViewModels;

/// <summary>
/// "Switch the device off and on": watches the USB devices and reports the first one that is there now but was not at
/// the look before. Generic HID names ("USB input device") tell a wheel from a pedal set badly; the device the user just
/// turned on is the one they mean.
/// </summary>
public sealed partial class UsbDetection(IUsbDeviceList usb, TimeProvider time) : ObservableObject
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    private CancellationTokenSource? _running;

    /// <summary>A detection runs; the picker shows what to do and how to stop.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    public partial bool IsDetecting { get; private set; }

    /// <summary>The last detection ran out without a device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    public partial bool FoundNothing { get; private set; }

    public string? Status => IsDetecting ? Loc.Instance["Usb_Detecting"] : FoundNothing ? Loc.Instance["Usb_DetectNone"] : null;

    /// <summary>Waits for a device to turn up; <c>null</c> after <see cref="Limit"/> or when stopped.</summary>
    public async Task<UsbDevice?> DetectAsync()
    {
        Stop();
        using var running = new CancellationTokenSource();
        _running = running;
        IsDetecting = true;
        FoundNothing = false;
        try
        {
            UsbDevice? found = await WaitForDeviceAsync(running.Token);
            FoundNothing = found is null;
            return found;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (_running == running)
            {
                _running = null;
                IsDetecting = false;
            }
        }
    }

    [RelayCommand]
    public void Stop() => _running?.Cancel();

    private async Task<UsbDevice?> WaitForDeviceAsync(CancellationToken cancellationToken)
    {
        HashSet<string> before = Ids(await ListAsync());
        DateTimeOffset giveUp = time.GetUtcNow() + Limit;
        while (time.GetUtcNow() < giveUp)
        {
            await Task.Delay(Interval, time, cancellationToken);
            IReadOnlyList<UsbDevice> now = await ListAsync();
            if (now.FirstOrDefault(d => !before.Contains(d.Id)) is { } added)
            {
                return added;
            }

            // Off counts too: a device that was on at the start is found once it comes back.
            before = Ids(now);
        }

        return null;
    }

    private async Task<IReadOnlyList<UsbDevice>> ListAsync()
    {
        try
        {
            return await Task.Run(usb.ConnectedDevices);
        }
        catch (Exception ex) when (ex is Win32Exception or COMException or InvalidOperationException)
        {
            return [];
        }
    }

    private static HashSet<string> Ids(IReadOnlyList<UsbDevice> devices) =>
        new(devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
}
