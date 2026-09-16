using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using RigShift.Windows.Audio;
using RigShift.Windows.Display;
using RigShift.Windows.Legacy;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// Usage: RigShift.Probe snapshot [--raw] | rates | audio | surround | apps | usb | usb-power <id> | keep-awake <seconds> | import <folder> | convert <folder> <target> | plan <folder> <profile>
// Output may contain device paths and endpoint IDs of this machine – do not paste it into public issues unredacted.

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

ILogger log = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Sink(new StderrSink())
    .CreateLogger();

var display = new CcdDisplayConfigurator(log, TimeProvider.System);
string command = args.Length > 0 ? args[0] : "snapshot";

switch (command)
{
    case "snapshot" when args.Length >= 2 && args[1] == "--raw":
        // The CCD input the snapshot is built from, for test fixtures (analysis finding L-04). Anonymise before committing.
        Print(CcdDisplayConfigurator.QueryRaw());
        break;

    case "snapshot":
        DisplaySnapshot snapshot = await display.QueryAsync(CancellationToken.None);
        Print(snapshot.Displays.Select(d => new { d.Identity, d.IsAvailable, d.IsActive, d.WindowsNumber, d.ActiveMode, Handle = d.NativeHandle.ToString() }));
        break;

    case "apps":
        // What the app picker offers: Start menu programs and running apps (finding HW-11).
        Print(RigShift.Windows.Apps.AppDiscovery.Find(log));
        break;

    case "rates":
        foreach (AttachedDisplay active in (await display.QueryAsync(CancellationToken.None)).Displays.Where(d => d.ActiveMode is not null))
        {
            DisplayAssignment mode = active.ActiveMode!;
            IReadOnlyList<RefreshRate> rates = await display.ListRefreshRatesAsync(active.Identity, mode.Width, mode.Height, CancellationToken.None);
            Print(new { active.Identity.FriendlyName, mode.Width, mode.Height, mode.Hdr, Rates = rates.Select(r => $"{r.Numerator}/{r.Denominator}") });
        }

        break;

    case "surround":
        {
            // Read-only: what the graphics driver reports about NVIDIA Surround, and which display ids it offers.
            using var surround = new NvSurroundController(display, log);
            SurroundState state = await surround.QueryAsync(CancellationToken.None);
            Print(new
            {
                state.Availability,
                state.IsActive,
                state.Message,
                state.Grids,
                Displays = await surround.ListDisplaysAsync(CancellationToken.None),
            });
            break;
        }

    case "audio":
        var audio = new PolicyConfigAudioController(log);
        Print(new
        {
            Render = await audio.ListAsync(AudioDirection.Render, CancellationToken.None),
            Capture = await audio.ListAsync(AudioDirection.Capture, CancellationToken.None),
        });
        break;

    case "desktop-icons":
        // Read-only: where the desktop symbols sit right now.
        RigShift.Core.Profiles.DesktopIconLayout? desktop = new RigShift.Windows.Shell.DesktopIcons(log).Capture();
        Print(desktop is null ? new { Reachable = false, Icons = (object?)null } : new { Reachable = true, Icons = (object?)desktop.Icons });
        break;

    case "desktop-icons-restore" when args.Length >= 2:
        // Changes something: reads the layout from a JSON file written by "desktop-icons" and puts the symbols there.
        var wanted = System.Text.Json.JsonSerializer.Deserialize<RigShift.Core.Profiles.DesktopIconLayout>(
            File.ReadAllText(args[1]), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Print(new RigShift.Windows.Shell.DesktopIcons(log).Restore(wanted!));
        break;

    case "usb":
        var usb = new RigShift.Windows.Apps.UsbDeviceList();
        Print(new { Present = usb.PresentDeviceIds().Order(StringComparer.Ordinal), Connected = usb.ConnectedDevices() });
        break;

    case "usb-power" when args.Length >= 2:
        // Read-only: selective suspend in the active power scheme and the device's power flags in the registry.
        RigShift.Core.Automation.UsbPowerFindings findings = new RigShift.Windows.Power.UsbPowerCheck(log).Check(args[1]);
        Print(new { Device = args[1], findings.SelectiveSuspendEnabledOnAc, findings.InstancesFound, findings.InstancesWithPowerSaving, Warn = RigShift.Core.Automation.UsbPowerSaving.ShouldWarn(findings) });
        break;

    case "keep-awake" when args.Length >= 2:
        // Holds the request for the given seconds; check it meanwhile with "powercfg /requests" (admin).
        using (var power = new RigShift.Windows.Power.PowerController(log))
        {
            power.SetKeepAwake(true);
            await Task.Delay(TimeSpan.FromSeconds(int.Parse(args[1], CultureInfo.InvariantCulture)));
            power.SetKeepAwake(false);
        }

        break;

    case "import" when args.Length >= 2:
        DisplaySnapshot live = await display.QueryAsync(CancellationToken.None);
        Print(LegacyProfileImporter.ImportFolder(args[1], live, log));
        break;

    case "convert" when args.Length >= 3:
        // One-off conversion of the author's script profiles into RigShift profile files (not a product feature).
        var store = new JsonProfileStore(args[2], log);
        foreach (LegacyImportResult result in LegacyProfileImporter.ImportFolder(args[1], live: null, log))
        {
            if (result.Profile is { } converted)
            {
                await store.SaveAsync(converted with { Icon = converted.Name.ToLowerInvariant() }, CancellationToken.None);
            }
        }

        break;

    case "plan" when args.Length >= 3:
        DisplaySnapshot current = await display.QueryAsync(CancellationToken.None);
        LegacyImportResult? imported = LegacyProfileImporter.ImportFolder(args[1], current, log)
            .FirstOrDefault(r => string.Equals(r.Name, args[2], StringComparison.OrdinalIgnoreCase));
        if (imported?.Profile is not { } profile)
        {
            Console.Error.WriteLine($"Profile '{args[2]}' not found or not importable.");
            return 4;
        }

        TopologyPlan plan = new TopologyPlanner(new TopologyPlannerOptions()).Plan(profile, current);
        Print(new
        {
            Resolved = plan.Resolved.Select(r => new { r.Assignment.Identity.FriendlyName, r.Assignment.Identity.TargetDevicePath, Handle = r.Target.NativeHandle.ToString() }),
            Missing = plan.Missing.Select(m => new { m.Assignment.Identity.FriendlyName, m.Assignment.Identity.TargetDevicePath, m.Assignment.IsOptional, m.Reason }),
            plan.Warnings,
            plan.IsBlocked,
            plan.ShouldRetryLater,
        });
        break;

    default:
        Console.Error.WriteLine("Usage: RigShift.Probe snapshot [--raw] | rates | audio | surround | desktop-icons | desktop-icons-restore <file> | usb | usb-power <id> | keep-awake <seconds> | import <folder> | convert <folder> <target> | plan <folder> <profile>");
        return 2;
}

return 0;

void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));

internal sealed class StderrSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{logEvent.Level.ToString()[..3].ToUpperInvariant()}] {logEvent.RenderMessage(CultureInfo.InvariantCulture)}"));
        if (logEvent.Exception is not null)
        {
            Console.Error.WriteLine(logEvent.Exception);
        }
    }
}
