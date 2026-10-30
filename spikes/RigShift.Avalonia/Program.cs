using Avalonia;
using RigShift.App.Services;
using Serilog;

namespace RigShift.Avalonia;

/// <summary>
/// Spike #155: the shared view models under Avalonia 12 with Semi/Ursa. Starts only on a data folder of its own
/// (RIGSHIFT_DATA_DIR, else a folder in %TEMP%), never on %AppData%\RigShift, and never switches a profile: the
/// display configurator is a preview that cannot apply anything (<see cref="PreviewDisplays"/>).
/// </summary>
/// <remarks>
/// Preview switches (environment): RIGSHIFT_PREVIEW_THEME=Light|Dark, RIGSHIFT_PREVIEW_PAGE=Overview|Profiles,
/// RIGSHIFT_PREVIEW_ACTIVE=&lt;profile&gt; (that profile's displays count as connected and active),
/// RIGSHIFT_PREVIEW_SELECT=&lt;profile&gt; (selected on the profiles page), RIGSHIFT_PREVIEW_EXPAND=Audio,Apps,Triggers,Behavior.
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack's install/update hooks run before anything else, like in the WPF app.
        Velopack.VelopackApp.Build().Run();
        string data = Environment.GetEnvironmentVariable("RIGSHIFT_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "rigshift-avalonia-spike");
        string appData = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigShift"));
        if (string.Equals(Path.GetFullPath(data).TrimEnd('\\'), appData, StringComparison.OrdinalIgnoreCase))
        {
            // The spike must never write to the real data.
            return 2;
        }

        Directory.CreateDirectory(data);
        App.Paths = new AppPaths(data);
        Log.Logger = AppLogging.Create(App.Paths);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Avalonia spike ended with an exception");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Used by the previewer as well.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
