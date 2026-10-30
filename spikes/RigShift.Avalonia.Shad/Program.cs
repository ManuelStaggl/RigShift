using Avalonia;
using RigShift.App.Services;
using Serilog;

namespace RigShift.Avalonia;

/// <summary>ShadUI variant of the spike's profile sheet; same data folder rules and preview switches as the Semi spike.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string data = Environment.GetEnvironmentVariable("RIGSHIFT_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "rigshift-avalonia-spike");
        string appData = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigShift"));
        if (string.Equals(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar), appData, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        Directory.CreateDirectory(data);
        ShadApp.Paths = new AppPaths(data);
        Log.Logger = AppLogging.Create(ShadApp.Paths);
        try
        {
            return AppBuilder.Configure<ShadApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
