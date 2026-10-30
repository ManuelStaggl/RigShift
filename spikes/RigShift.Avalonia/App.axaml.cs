using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using RigShift.Avalonia.Services;
using Serilog;

namespace RigShift.Avalonia;

/// <summary>The Semi/Ursa spike app: services from <see cref="SpikeHost"/>, the window with rail and pages.</summary>
public sealed class App : Application, IAppShell
{
    private ServiceProvider? _services;
    private MainWindow? _window;

    internal static AppPaths Paths { get; set; } = new(Path.Combine(Path.GetTempPath(), "rigshift-avalonia-spike"));

    internal static IServiceProvider Provider => ((App)Current!)._services ?? throw new InvalidOperationException("Services not built.");

    public bool IsExiting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_THEME") switch
        {
            "Light" => ThemeVariant.Light,
            _ => ThemeVariant.Dark,
        };
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        Log.Information("RigShift Avalonia spike starting, data directory {DataDirectory}", Paths.DataDirectory);
        try
        {
            _services = await SpikeHost.StartAsync(Paths, this);

            AppPage page = Enum.TryParse(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_PAGE"), out AppPage p) ? p : AppPage.Overview;
            _window = new MainWindow();
            desktop.MainWindow = _window;
            _window.Show();
            _window.Navigate(page);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Avalonia spike failed to start");
            desktop.Shutdown(1);
        }
    }

    public void ShowMainWindow(AppPage? page = null)
    {
        _window?.Show();
        if (page is { } shown)
        {
            _window?.Navigate(shown);
        }
    }

    public void IdentifyDisplays(IReadOnlyList<(int Number, string Name, DisplayAssignment Mode)> displays) =>
        Log.Information("Spike: identify {Count} displays (no overlay windows yet)", displays.Count);

    public void Quit()
    {
        IsExiting = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        _services?.Dispose();
    }

    public void QuitByUser() => Quit();
}
