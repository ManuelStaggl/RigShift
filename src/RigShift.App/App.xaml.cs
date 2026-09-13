using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Storage;
using RigShift.Windows.Audio;
using RigShift.Windows.Display;
using Serilog;

namespace RigShift.App;

public partial class App : Application
{
    private IHost? _host;

    public static string DataDirectory { get; } =
        Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigShift"));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(DataDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(DataDirectory, "logs", "rigshift-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Log.Logger);
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IProfileStore>(_ =>
                    new JsonProfileStore(Path.Combine(DataDirectory, "profiles"), Log.Logger));
                services.AddSingleton<IDisplayConfigurator, CcdDisplayConfigurator>();
                services.AddSingleton<IAudioController, PolicyConfigAudioController>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _host.Start();
        Log.Information("RigShift {Version} started, data directory {DataDirectory}",
            typeof(App).Assembly.GetName().Version, DataDirectory);

        // Skeleton: show the main window. v1 replaces this with the tray shell (see docs/PLAN.md, M3).
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("RigShift exiting with code {ExitCode}", e.ApplicationExitCode);
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
