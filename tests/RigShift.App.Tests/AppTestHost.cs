using System.IO;
using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Settings;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;

namespace RigShift.App.Tests;

/// <summary>The real app services on fakes: a temp data folder, in-memory profiles, a scripted display boundary.</summary>
internal sealed class AppTestHost : IDisposable
{
    public AppTestHost(FakeDisplayConfigurator display)
    {
        Display = display;
        Paths = new AppPaths(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-app-tests", Guid.NewGuid().ToString("N"))));
        Settings = new SettingsService(new JsonSettingsStore(Paths.SettingsFile, Logger.None), Substitute.For<IAutostart>());
        var planner = new TopologyPlanner(new TopologyPlannerOptions());
        Catalog = new ProfileCatalog(Store, display, new ActiveProfileMatcher(planner), Settings, Paths, Logger.None);
        Orchestrator = new SwitchOrchestrator(
            display,
            Substitute.For<IAudioController>(),
            Substitute.For<IAppLauncher>(),
            Usb,
            new FakePowerController(),
            new FakeDuckingPreference(),
            new InMemoryDuckingMemory(),
            Substitute.For<IWindowRescuer>(),
            Substitute.For<IDesktopIcons>(),
            Surround,
            Confirmation,
            new InMemorySwitchJournal(),
            planner,
            new SwitchOptions { WindowRescueDelay = TimeSpan.Zero },
            new AutoAdvanceTimeProvider(),
            Logger.None);
        Coordinator = new SwitchCoordinator(Orchestrator, Catalog, Settings, TimeProvider.System, Logger.None);
    }

    public FakeDisplayConfigurator Display { get; }

    public AppPaths Paths { get; }

    public InMemoryProfileStore Store { get; } = new();

    public IUsbDeviceList Usb { get; } = Substitute.For<IUsbDeviceList>();

    public IFullscreenCheck Fullscreen { get; } = Substitute.For<IFullscreenCheck>();

    internal FakeSessionWatch Session { get; } = new();

    public ISwitchConfirmation Confirmation { get; } = Substitute.For<ISwitchConfirmation>();

    public FakeSurroundController Surround { get; } = new();

    public SettingsService Settings { get; }

    public ProfileCatalog Catalog { get; }

    public SwitchOrchestrator Orchestrator { get; }

    public SwitchCoordinator Coordinator { get; }

    public void Dispose()
    {
        Coordinator.Dispose();
        Settings.Dispose();
        // A view model may still be saving in the background; its temp file is gone a moment later.
        for (int attempt = 0; Directory.Exists(Paths.DataDirectory); attempt++)
        {
            try
            {
                Directory.Delete(Paths.DataDirectory, recursive: true);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}
