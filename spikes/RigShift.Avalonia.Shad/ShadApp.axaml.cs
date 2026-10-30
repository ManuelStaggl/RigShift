using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Services;
using RigShift.Avalonia.Services;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Avalonia;

/// <summary>The ShadUI comparison app: the same services as the Semi spike, one window with the profile sheet.</summary>
public sealed class ShadApp : Application, IAppShell
{
    private ServiceProvider? _services;

    internal static AppPaths Paths { get; set; } = new(Path.Combine(Path.GetTempPath(), "rigshift-avalonia-spike"));

    internal static IServiceProvider Provider => ((ShadApp)Current!)._services ?? throw new InvalidOperationException("Services not built.");

    public bool IsExiting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_THEME") == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        desktop.ShutdownMode = global::Avalonia.Controls.ShutdownMode.OnMainWindowClose;
        try
        {
            _services = await SpikeHost.StartAsync(Paths, this);
            var window = new ShadWindow();
            desktop.MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "ShadUI spike failed to start");
            desktop.Shutdown(1);
        }
    }

    public void ShowMainWindow(AppPage? page = null)
    {
    }

    public void IdentifyDisplays(IReadOnlyList<(int Number, string Name, DisplayAssignment Mode)> displays) =>
        Log.Information("Spike: identify {Count} displays", displays.Count);

    public void Quit()
    {
        IsExiting = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        _services?.Dispose();
    }

    public void QuitByUser() => Quit();
}
