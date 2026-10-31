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
/// RIGSHIFT_PREVIEW_TOPOLOGY=1|&lt;png path&gt; (<see cref="TopologyGallery"/>), RIGSHIFT_PREVIEW_CRASH=1 (an exception every
/// 300 ms, so the repeated-error dialog opens); <c>--preview-confirmation</c> opens the countdown.
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

    public static void AfterStart(App app, TrayIconService tray, bool confirmation)
    {
        // The countdown cannot be reached on a machine without the profiles' displays: two stored profiles, or the
        // live arrangement twice, so both pictures show something.
        if (confirmation)
        {
            ProfileCatalog catalog = App.Provider.GetRequiredService<ProfileCatalog>();
            IReadOnlyList<RigShift.Core.Profiles.Profile> profiles = catalog.Profiles;
            ConfirmationView view = profiles.Count >= 2
                ? new ConfirmationView(profiles[1].Id, TopologyDisplays.From(profiles[0].Displays), profiles[0].Name, TopologyDisplays.From(profiles[1].Displays), profiles[1].Name)
                : new ConfirmationView(Guid.Empty, [], "Desk", [], "Sim Rig");
            _ = Views.ConfirmationWindow.ShowAsync(view, TimeSpan.FromSeconds(12), CancellationToken.None);
        }

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

        // A timer that throws on every tick, which is what a broken layout pass or binding looks like.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_CRASH") is { Length: > 0 })
        {
            // Thrown from a posted operation, which reports to the UI thread's handler; one thrown in the tick itself
            // stops the timer, and InvokeAsync keeps its exception in the returned operation.
            var broken = new global::Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            broken.Tick += (_, _) => global::Avalonia.Threading.Dispatcher.UIThread.Post(
                () => throw new InvalidOperationException("Preview: this timer throws on every tick."));
            broken.Start();
        }

        // After the window, which would take the focus and so close the popup.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TRAY") == "1")
        {
            tray.ShowPopup();
        }
    }
}
