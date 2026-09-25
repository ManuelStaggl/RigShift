using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Velopack.Logging;

namespace RigShift.App.Services;

public static class AppLogging
{
    /// <summary>Serilog's default would silently stop writing a day's file at 1 GB; a new file is started instead.</summary>
    private const long FileSizeLimitBytes = 50L * 1024 * 1024;

    /// <summary>Information until the settings say otherwise; the logger exists before the settings are read.</summary>
    private static readonly LoggingLevelSwitch Level = new(LogEventLevel.Information);

    private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({ProcessId}) {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>The setting "detailed log": Debug lines on or off, effective at once.</summary>
    public static void SetDetailed(bool detailed)
    {
        LogEventLevel wanted = detailed ? LogEventLevel.Debug : LogEventLevel.Information;
        if (Level.MinimumLevel != wanted)
        {
            Level.MinimumLevel = wanted;
            Log.Information("Log level is now {Level}", wanted);
        }
    }

    /// <summary>
    /// Daily log file, 14 days. <c>shared</c> because the tray app and a command line process write at the same time.
    /// Created first thing in <c>Main</c>, so Velopack's install and update steps land in the same file (analysis finding F-03).
    /// </summary>
    public static ILogger Create(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(Level)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            // Static Log.* calls have no context of their own; "App" rather than an empty column (v4 finding E-17).
            .Enrich.WithProperty("SourceContext", "App");
#if DEBUG
        // An OutputDebugString per line helps nobody in a release build.
        configuration = configuration.WriteTo.Debug(formatProvider: System.Globalization.CultureInfo.InvariantCulture);
#endif
        return configuration
            .WriteTo.File(
                new LogLineFormatter(OutputTemplate),
                Path.Combine(paths.Logs, "rigshift-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
    }
}

/// <summary>Velopack's install, update and uninstall messages in the RigShift log.</summary>
public sealed class SerilogVelopackLogger(ILogger log) : IVelopackLogger
{
    private readonly ILogger _log = log.ForContext("SourceContext", "Velopack");

    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        LogEventLevel level = logLevel switch
        {
            VelopackLogLevel.Trace => LogEventLevel.Verbose,
            VelopackLogLevel.Debug => LogEventLevel.Debug,
            VelopackLogLevel.Information => LogEventLevel.Information,
            VelopackLogLevel.Warning => LogEventLevel.Warning,
            VelopackLogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Fatal,
        };

        // Every start that is not an installed copy (portable folder, development build) says so; not worth a warning.
        if (level == LogEventLevel.Warning && message?.StartsWith("Failed to initialize WindowsVelopackLocator", StringComparison.Ordinal) == true)
        {
            level = LogEventLevel.Debug;
        }

        // The message comes from Velopack, not a template: pass it as a property so braces in paths stay literal.
        _log.Write(level, exception, "{VelopackMessage}", message);
    }
}
