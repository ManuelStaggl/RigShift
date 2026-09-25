#if DEBUG
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Settings;
using RigShift.Core.Storage;
using Serilog;

namespace RigShift.App;

/// <summary>
/// Developer aids, Debug builds only: windows, dialogs and pages in states that need hardware, a menu or a file to reach,
/// for screenshots and visual checks. Each is switched on by a <c>--preview-…</c> argument or a <c>RIGSHIFT_PREVIEW_…</c>
/// variable.
/// </summary>
public partial class App
{
    private static readonly System.Text.Json.JsonSerializerOptions PreviewJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The component gallery as PNG; it needs the resources only, so nothing else starts.</summary>
    /// <returns><c>true</c> when the gallery was written and the app should end.</returns>
    private bool WriteGalleryPreview()
    {
        if (_request.PreviewGallery is not { } galleryDirectory)
        {
            return false;
        }

        GalleryPreview.Write(galleryDirectory);
        return true;
    }

    /// <summary>Demo profiles whose monitors are not on this machine, so the pages can be shown in a working state.</summary>
    private static void AddPreviewServices(IServiceCollection services)
    {
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_DISPLAYS") is { Length: > 0 } previewActive)
        {
            services.Replace(ServiceDescriptor.Singleton<IDisplayConfigurator>(
                sp => new PreviewDisplayConfigurator(sp.GetRequiredService<IProfileStore>(), previewActive)));

            // A dev build is never in the Run key; without this every trigger tab warns that nothing starts with Windows.
            services.Replace(ServiceDescriptor.Singleton<IAutostart>(new PreviewAutostart()));
            AppEditItem.PreviewPathsExist = true;
        }
    }

    private sealed class PreviewAutostart : IAutostart
    {
        public bool IsEnabled { get; private set; } = true;

        public void SetEnabled(bool enabled) => IsEnabled = enabled;
    }

    /// <summary>Started once profiles and games are loaded, before the main window opens.</summary>
    private async Task StartPreviewsAsync(ProfileCatalog catalog)
    {
        // The confirmation window cannot be reached on a machine without the profile's displays.
        if (_request.PreviewConfirmation)
        {
            // Two of the stored profiles, or the live arrangement twice, so both pictures show something.
            ConfirmationResult answer = await ConfirmationWindow.ShowAsync(
                await PreviewConfirmationAsync(catalog), TimeSpan.FromSeconds(12), CancellationToken.None);
            Log.Information("Confirmation preview answered {Answer}", answer);
        }

        // The tray popup and the tray icon for other taskbars and DPI steps cannot be captured over RDP.
        if (_request.PreviewBranding is { } previewDirectory)
        {
            BrandingPreview.Show(previewDirectory, Services.GetRequiredService<TrayPopupViewModel>());
        }

        // The app picker with a demo list (JSON array of name, path, isRunning) for README screenshots.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_APPS") is { Length: > 0 } demoApps)
        {
            AppPickerWindow.Pick(null, null, () => System.Text.Json.JsonSerializer.Deserialize<List<Windows.Apps.DiscoveredApp>>(
                File.ReadAllText(demoApps), PreviewJson) ?? []);
        }

        // The game picker and the window capture, which otherwise only open from inside the editor. "multi" opens the
        // picker the way the games page does, with tick boxes for several games at once.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_GAMEPICKER") is { Length: > 0 } picker)
        {
            GameDialogs dialogs = Services.GetRequiredService<GameDialogs>();
            if (string.Equals(picker, "multi", StringComparison.OrdinalIgnoreCase))
            {
                _ = GamePickerWindow.PickManyAsync(null, dialogs);
            }
            else
            {
                _ = GamePickerWindow.PickAsync(null, dialogs);
            }
        }

        // The question dialogs, which otherwise need a profile and a menu (or a backup file) to reach.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_DIALOG") is { Length: > 0 } dialogKind)
        {
            _ = Dispatcher.InvokeAsync(() => dialogKind.ToUpperInvariant() switch
            {
                "UNSAVED" => ProfileDialogs.ConfirmUnsavedAsync("Sim Rig", "Schreibtisch").ContinueWith(_ => { }, TaskScheduler.Default),
                "RESTORE" => ProfileDialogs.ConfirmRestoreAsync(PreviewBackup()).ContinueWith(_ => { }, TaskScheduler.Default),
                _ => ProfileDialogs.ConfirmDeleteAsync("Rig · Dreifach", ruleCount: 1).ContinueWith(_ => { }, TaskScheduler.Default),
            });
        }

        // A timer that throws on every tick, which is what a broken layout pass or binding looks like.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_CRASH") is { Length: > 0 })
        {
            var broken = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            // Thrown from a queued call: a timer whose own tick throws is never re-armed by WPF.
            broken.Tick += (_, _) => Dispatcher.BeginInvoke(
                () => throw new InvalidOperationException("Preview: this timer throws on every tick."));
            broken.Start();
        }

        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_WINDOWCAPTURE") is { Length: > 0 })
        {
            _ = Dispatcher.InvokeAsync(() => WindowCaptureWindow.Capture(null, Services.GetRequiredService<GameDialogs>(), null));
        }
    }

    /// <summary>Started once the main window is open: the games page as PNG, or the assistant at a later step.</summary>
    /// <returns><c>true</c> when the assistant opens as a preview, so the first-start assistant stays away.</returns>
    private bool StartWindowPreviews()
    {
        // The games page in its states, rendered from the visual tree (works over disconnected RDP).
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_PAGE") is { Length: > 0 } pageDirectory)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await PagePreview.WriteGamesAsync(Services, pageDirectory);
                Quit();
            });
        }

        // The assistant at a later step with demo profiles (second, trigger, done).
        if (!Enum.TryParse(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SETUP"), ignoreCase: true, out SetupStep previewStep))
        {
            return false;
        }

        // Logged, not discarded: a XAML error in the assistant would otherwise leave no trace at all.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Services.GetRequiredService<ProfileDialogs>().ShowSetupAssistantAsync(previewStep);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Setup assistant preview failed");
            }
        });
        return true;
    }

    /// <summary>A backup with everything the restore question warns about: a share, a bare name, a rule without the question.</summary>
    private BackupContent PreviewBackup()
    {
        IReadOnlyList<Core.Profiles.Profile> profiles = Services.GetRequiredService<ProfileCatalog>().Profiles;
        if (profiles.Count == 0)
        {
            return new BackupContent([], null);
        }

        Core.Profiles.Profile first = profiles[0] with
        {
            Apps =
            [
                new Core.Profiles.AppAction { Path = @"C:\Program Files (x86)\SimHub\SimHubWPF.exe" },
                new Core.Profiles.AppAction { Path = @"\\nas\tools\CrewChiefV4.exe", Arguments = "-profile rig" },
                new Core.Profiles.AppAction { Path = "overlay.exe" },
                new Core.Profiles.AppAction { Kind = Core.Profiles.AppActionKind.Stop, Path = "Discord.exe" },
            ],
        };
        var settings = new AppSettings
        {
            AutomationRules =
            [
                new Core.Automation.AutomationRule { ProfileId = first.Id, Devices = [new Core.Automation.RuleDevice { Id = "VID_0EB7&PID_0006", Name = "Fanatec Wheel Base" }], SkipConfirmation = true },
            ],
        };
        return new BackupContent([first, .. profiles.Skip(1)], settings);
    }

    /// <summary>A filled confirmation view from the stored profiles, or from the live arrangement.</summary>
    private async Task<ConfirmationView> PreviewConfirmationAsync(ProfileCatalog catalog)
    {
        IReadOnlyList<Core.Profiles.Profile> profiles = catalog.Profiles;
        if (profiles.Count >= 2)
        {
            return new ConfirmationView(
                profiles[1].Id,
                TopologyDisplays.From(profiles[0].Displays), profiles[0].Name,
                TopologyDisplays.From(profiles[1].Displays), profiles[1].Name);
        }

        Core.Topology.DisplaySnapshot now = await Services.GetRequiredService<IDisplayConfigurator>().QueryAsync(CancellationToken.None);
        IReadOnlyList<Core.Topology.TopologyDisplay> live = TopologyDisplays.From(Core.Profiles.ProfileEditing.CurrentArrangement(now, []));
        return new ConfirmationView(Guid.Empty, live, "Schreibtisch", live, "Sim Rig");
    }
}
#endif
