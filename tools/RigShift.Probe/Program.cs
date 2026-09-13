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

// Usage: RigShift.Probe snapshot | audio | import <folder> | plan <folder> <profile>
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
    case "snapshot":
        DisplaySnapshot snapshot = await display.QueryAsync(CancellationToken.None);
        Print(snapshot.Displays.Select(d => new { d.Identity, d.IsAvailable, d.IsActive, d.ActiveMode, Handle = d.NativeHandle.ToString() }));
        break;

    case "audio":
        var audio = new PolicyConfigAudioController(log);
        Print(new
        {
            Render = await audio.ListAsync(AudioDirection.Render, CancellationToken.None),
            Capture = await audio.ListAsync(AudioDirection.Capture, CancellationToken.None),
        });
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
        Console.Error.WriteLine("Usage: RigShift.Probe snapshot | audio | import <folder> | plan <folder> <profile>");
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
