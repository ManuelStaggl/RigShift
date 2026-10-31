using Avalonia.VisualTree;
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
/// RIGSHIFT_PREVIEW_PALETTE=&lt;query&gt;, RIGSHIFT_PREVIEW_GAMEPICKER=single|multi, RIGSHIFT_PREVIEW_WINDOWCAPTURE=1, RIGSHIFT_PREVIEW_IDENTIFY=1, RIGSHIFT_PREVIEW_TRAY=1,
/// RIGSHIFT_PREVIEW_TOPOLOGY=1|&lt;png path&gt; (<see cref="TopologyGallery"/>), RIGSHIFT_PREVIEW_CRASH=1 (an exception every
/// 300 ms, so the repeated-error dialog opens), RIGSHIFT_PREVIEW_SETUP=welcome|second|trigger|done (the setup assistant at
/// that step, nothing saved before the user clicks), RIGSHIFT_PREVIEW_SCROLL=&lt;pixels&gt; (scrolls the shown page),
/// RIGSHIFT_PREVIEW_DIALOG=unsaved|delete|restore (the question with demo content; nothing is changed),
/// RIGSHIFT_PREVIEW_APPS=&lt;json&gt; (the app picker with a demo list);
/// <c>--preview-confirmation</c> opens the countdown.
/// </summary>
internal static class Previews
{
    private static readonly System.Text.Json.JsonSerializerOptions DemoJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static void Register(IServiceCollection services)
    {
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_DISPLAYS") is { Length: > 0 } active)
        {
            services.Replace(ServiceDescriptor.Singleton<IDisplayConfigurator>(
                sp => new PreviewDisplayConfigurator(sp.GetRequiredService<IProfileStore>(), active)));
        }

        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_INERT") == "1")
        {
            InertPreview.Register(services);
        }

        // RIGSHIFT_PREVIEW_OPENXR=1: two runtimes, so the game editor offers its OpenXR choice on a machine without VR.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_OPENXR") == "1")
        {
            services.Replace(ServiceDescriptor.Singleton<IOpenXrRuntimes>(new PreviewOpenXrRuntimes()));
        }
    }

    /// <returns><c>true</c> when the assistant opens as a preview, so the first-start assistant stays away.</returns>
    public static bool AfterStart(App app, TrayIconService tray, bool confirmation)
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

        // A page taller than the window, scrolled for a second screenshot; after the page has laid out.
        if (double.TryParse(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SCROLL"), System.Globalization.CultureInfo.InvariantCulture, out double scroll)
            && app.Window is { } scrolled)
        {
            global::Avalonia.Threading.DispatcherTimer.RunOnce(
                () =>
                {
                    if ((scrolled.Host.Content as global::Avalonia.Visual)?.GetVisualDescendants().OfType<global::Avalonia.Controls.ScrollViewer>().FirstOrDefault() is { } scroller)
                    {
                        scroller.Offset = new global::Avalonia.Vector(0, scroll);
                    }
                },
                TimeSpan.FromSeconds(2));
        }

        // The questions, answered by nobody: nothing is restored, deleted or discarded.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_DIALOG") is { Length: > 0 } dialog)
        {
            Services.PageDialogs dialogs = App.Provider.GetRequiredService<Services.PageDialogs>();
            global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    _ = dialog.ToUpperInvariant() switch
                    {
                        "UNSAVED" => (object)await dialogs.ConfirmUnsavedAsync("Sim Rig", "Desk"),
                        "DELETE" => await dialogs.ConfirmDeleteAsync("Sim Rig", ruleCount: 1),
                        _ => await dialogs.ConfirmRestoreAsync(PreviewBackup()),
                    };
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Dialog preview failed");
                }
            });
        }

        // The app picker with a demo list (JSON array of name, path, isRunning) for README screenshots.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_APPS") is { Length: > 0 } demoApps)
        {
            _ = Views.AppPickerWindow.PickAsync(null, () => System.Text.Json.JsonSerializer.Deserialize<List<RigShift.Windows.Apps.DiscoveredApp>>(
                File.ReadAllText(demoApps), DemoJson) ?? []);
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

        // The game picker and the window capture, which otherwise only open from inside the games page. "multi" opens the
        // picker with tick boxes; RIGSHIFT_PREVIEW_GAMES=<json> gives it a demo library on a machine without stores.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_GAMEPICKER") is { Length: > 0 } picker)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    GameEditorFactory factory = App.Provider.GetRequiredService<GameEditorFactory>();
                    _ = string.Equals(picker, "multi", StringComparison.OrdinalIgnoreCase)
                        ? (object)await Views.GamePickerWindow.PickManyAsync(factory)
                        : await Views.GamePickerWindow.PickAsync(factory);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Game picker preview failed");
                }
            });
        }

        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_WINDOWCAPTURE") is { Length: > 0 })
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Views.WindowCaptureWindow.CaptureAsync(App.Provider.GetRequiredService<GameEditorFactory>().OpenWindows(), null);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Window capture preview failed");
                }
            });
        }

        // After the window, which would take the focus and so close the popup.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TRAY") == "1")
        {
            tray.ShowPopup();
        }

        // The assistant at a step with demo profiles made from the current arrangement: welcome, second, trigger, done.
        if (!Enum.TryParse(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SETUP"), ignoreCase: true, out SetupStep step))
        {
            return false;
        }

        // Logged, not discarded: a XAML error in the assistant would otherwise leave no trace at all.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await App.Provider.GetRequiredService<Services.PageDialogs>().ShowSetupAssistantAsync(step == SetupStep.Welcome ? null : step);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Setup assistant preview failed");
            }
        });
        return true;
    }

    /// <summary>The stored profiles as a backup, the first one with programs and a rule that each need a second look.</summary>
    private static RigShift.Core.Storage.BackupContent PreviewBackup()
    {
        IReadOnlyList<RigShift.Core.Profiles.Profile> profiles = App.Provider.GetRequiredService<ProfileCatalog>().Profiles;
        if (profiles.Count == 0)
        {
            return new RigShift.Core.Storage.BackupContent([], null);
        }

        RigShift.Core.Profiles.Profile first = profiles[0] with
        {
            Apps =
            [
                new RigShift.Core.Profiles.AppAction { Path = @"C:\Program Files (x86)\SimHub\SimHubWPF.exe" },
                new RigShift.Core.Profiles.AppAction { Path = @"\\nas\tools\CrewChiefV4.exe", Arguments = "-profile rig" },
                new RigShift.Core.Profiles.AppAction { Path = "overlay.exe" },
                new RigShift.Core.Profiles.AppAction { Kind = RigShift.Core.Profiles.AppActionKind.Stop, Path = "Discord.exe" },
            ],
        };
        var settings = new RigShift.Core.Settings.AppSettings
        {
            AutomationRules =
            [
                new RigShift.Core.Automation.AutomationRule { ProfileId = first.Id, Devices = [new RigShift.Core.Automation.RuleDevice { Id = "VID_0EB7&PID_0006", Name = "Fanatec Wheel Base" }], SkipConfirmation = true },
            ],
        };
        return new RigShift.Core.Storage.BackupContent([first, .. profiles.Skip(1)], settings);
    }
}

/// <summary>Demo OpenXR runtimes for screenshots; nothing is read or written.</summary>
internal sealed class PreviewOpenXrRuntimes : IOpenXrRuntimes
{
    public IReadOnlyList<OpenXrRuntime> Installed() =>
    [
        new(@"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json", "SteamVR", IsActive: true),
        new(@"C:\Program Files\Oculus\Support\oculus-runtime\oculus_openxr_64.json", "Oculus", IsActive: false),
    ];
}
