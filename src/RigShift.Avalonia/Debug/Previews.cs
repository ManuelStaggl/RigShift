using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Tray;
using RigShift.Core.Abstractions;

namespace RigShift.Avalonia.Debug;

/// <summary>
/// Debug-only hooks for screenshots, set in the environment: RIGSHIFT_PREVIEW_DISPLAYS=&lt;profile&gt; (every profile's
/// displays read as attached, that profile's active; nothing is ever applied), RIGSHIFT_PREVIEW_PAGE=&lt;page&gt;,
/// RIGSHIFT_PREVIEW_PALETTE=&lt;query&gt;, RIGSHIFT_PREVIEW_IDENTIFY=1, RIGSHIFT_PREVIEW_TRAY=1,
/// RIGSHIFT_PREVIEW_TOPOLOGY=1|&lt;png path&gt; (<see cref="TopologyGallery"/>).
/// </summary>
internal static class Previews
{
    public static void Register(IServiceCollection services)
    {
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_DISPLAYS") is { Length: > 0 } active)
        {
            services.Replace(ServiceDescriptor.Singleton<IDisplayConfigurator>(
                sp => new PreviewDisplayConfigurator(sp.GetRequiredService<IProfileStore>(), active)));
        }
    }

    public static void AfterStart(App app, TrayIconService tray)
    {
        if (Enum.TryParse(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_PAGE"), out AppPage page))
        {
            app.ShowMainWindow(page);
        }

        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_PALETTE") is { } query && app.Window is { } window)
        {
            window.OpenPalette();
            window.PaletteQuery.Text = query;
        }

        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_IDENTIFY") == "1")
        {
            // After the overview has read the displays.
            global::Avalonia.Threading.DispatcherTimer.RunOnce(
                () => App.Provider.GetRequiredService<OverviewViewModel>().IdentifyCommand.Execute(null),
                TimeSpan.FromSeconds(6));
        }

        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TOPOLOGY") is { Length: > 0 } topology)
        {
            TopologyGallery.Show(
                App.Provider.GetRequiredService<ProfileCatalog>().Items.Select(i => (i.Name, i.Topology)),
                topology.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? topology : null);
        }

        // After the window, which would take the focus and so close the popup.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TRAY") == "1")
        {
            tray.ShowPopup();
        }
    }
}
