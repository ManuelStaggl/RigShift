using System.Globalization;
using System.IO;
using Serilog;

namespace RigShift.App.Services;

public static class AppLogging
{
    /// <summary>
    /// Daily log file, 14 days. <c>shared</c> because the tray app and a command line process write at the same time.
    /// </summary>
    public static ILogger Create(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(paths.Logs, "rigshift-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({ProcessId}) {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
