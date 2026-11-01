using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Updates;
using Serilog.Core;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>
/// The app's own container with demo data – a desk and a rig profile, one game – and nothing that reaches hardware,
/// the registry or the network. For tests of pages and view models that are too wired up to build by hand.
/// </summary>
internal sealed class DemoServices : IDisposable
{
    public DemoServices()
    {
        Paths = new AppPaths(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-app-tests", Guid.NewGuid().ToString("N"))));
        Desk = Profile("Desk", DeskModes, confirm: true);
        Rig = Core.Tests.TestDisplays.Rig(confirm: true) with
        {
            Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 },
        };
        Profiles.Profiles.AddRange([Desk, Rig]);
        Games.Games.Add(new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = "iRacing",
            Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
            ProfileId = Rig.Id,
        });
        Audio.ListAsync(default, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<AudioDeviceInfo>>([]));
        Usb.ConnectedDevices().Returns([]);
        Usb.PresentDeviceIds().Returns(new HashSet<string>());
    }

    public AppPaths Paths { get; }

    public Profile Desk { get; }

    public Profile Rig { get; }

    public InMemoryProfileStore Profiles { get; } = new();

    public InMemoryGameStore Games { get; } = new();

    /// <summary>The desk is on screen; the rig's ultrawide and tablet are connected but off.</summary>
    public IDisplayConfigurator Display { get; set; } = new FakeDisplayConfigurator(DeskActive());

    public IAudioController Audio { get; } = Substitute.For<IAudioController>();

    public IUsbDeviceList Usb { get; } = Substitute.For<IUsbDeviceList>();

    public IServiceCollection Services() =>
        new ServiceCollection()
            .AddRigShift(Paths, Substitute.For<IAppShell>(), Logger.None)
            .Replace(ServiceDescriptor.Singleton<IProfileStore>(Profiles))
            .Replace(ServiceDescriptor.Singleton<IGameStore>(Games))
            .Replace(ServiceDescriptor.Singleton(Display))
            .Replace(ServiceDescriptor.Singleton(Audio))
            .Replace(ServiceDescriptor.Singleton(Usb))
            .Replace(ServiceDescriptor.Singleton(Substitute.For<IAutostart>()))
            .Replace(ServiceDescriptor.Singleton(Substitute.For<IUsbPowerCheck>()))
            .Replace(ServiceDescriptor.Singleton(Substitute.For<IDisplaySizeReader>()))
            .Replace(ServiceDescriptor.Singleton(Substitute.For<IGameLibrary>()))
            .Replace(ServiceDescriptor.Singleton(Substitute.For<IUpdateFeed>()))
            .Replace(ServiceDescriptor.Singleton(Substitute.For<IUpdatePolicy>()))
            .Replace(ServiceDescriptor.Singleton<ISurroundController>(new FakeSurroundController()))
            .Replace(ServiceDescriptor.Singleton<IDesktopIcons>(new FakeDesktopIcons()))
            .Replace(ServiceDescriptor.Singleton<IDuckingPreference>(new FakeDuckingPreference()))
            .Replace(ServiceDescriptor.Singleton<ISessionWatch>(new FakeSessionWatch()));

    /// <summary>The container with settings, profiles and games loaded, as the app has it after its start.</summary>
    public async Task<ServiceProvider> StartAsync()
    {
        ServiceProvider provider = Services().BuildServiceProvider();
        await provider.GetRequiredService<SettingsService>().LoadAsync(CancellationToken.None);
        await provider.GetRequiredService<ProfileCatalog>().ReloadAsync(CancellationToken.None);
        await provider.GetRequiredService<GameCatalog>().ReloadAsync(CancellationToken.None);
        return provider;
    }

    public void Dispose()
    {
        if (Directory.Exists(Paths.DataDirectory))
        {
            Directory.Delete(Paths.DataDirectory, recursive: true);
        }
    }
}
