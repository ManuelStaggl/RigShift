using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Updates;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>
/// The main window with every page and every tab, the setup assistant, a question dialog and the tray popup, with demo
/// data, in English and German, 1080 and 1920 px wide. A binding path that does not exist, a template that throws or a
/// layout that never settles fails here instead of on someone's screen (v4 finding E-04). The compiler sees none of it.
/// WPF allows one application object per process, so everything runs in this one test, on one thread.
/// </summary>
public sealed class XamlSmokeTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private readonly AppPaths _paths = new(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-app-tests", Guid.NewGuid().ToString("N"))));
    private readonly BindingErrors _errors = new();
    private readonly ConcurrentQueue<string> _crashes = new();

    public void Dispose()
    {
        _errors.Dispose();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EveryPageAndWindow_ShowsWithoutBindingErrors()
    {
        using var thread = new DispatcherThread();
        CultureSnapshot language = CultureSnapshot.Take();
        try
        {
            await thread.RunAsync(ShowEverythingAsync);
        }
        finally
        {
            thread.Invoke(() => language.Restore());
        }

        _crashes.ShouldBeEmpty();
        _errors.Lines.ShouldBeEmpty();
    }

    private async Task ShowEverythingAsync()
    {
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            _crashes.Enqueue(e.Exception.ToString());
            e.Handled = true;
        };

        // Relative pack URIs (window icons, token dictionaries) point into the app's assembly, not the test runner's. The
        // setter refuses once anything in the process has read the property, so the field is set directly.
        typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .ShouldNotBeNull("WPF keeps the resource assembly in this field")
            .SetValue(null, typeof(App).Assembly);
        var app = new App(new CliRequest(), _paths);
        app.InitializeComponent();
        using ServiceProvider services = Services().BuildServiceProvider();
        await services.GetRequiredService<SettingsService>().LoadAsync(CancellationToken.None);
        await services.GetRequiredService<ProfileCatalog>().ReloadAsync(CancellationToken.None);
        await services.GetRequiredService<GameCatalog>().ReloadAsync(CancellationToken.None);

        MainWindow main = services.GetRequiredService<MainWindow>();
        Offscreen(main);
        main.Show();
        await IdleAsync();

        foreach (string language in (string[])["en", "de"])
        {
            Loc.Instance.SetLanguage(language);
            foreach (double width in (double[])[1080, 1920])
            {
                main.Width = width;
                foreach (MainWindow.NavEntry entry in MainWindow.Pages)
                {
                    main.ShowPage(entry.Page);
                    await IdleAsync();
                    await EveryTabAsync(main);
                }
            }

            await WithDialogsClosedAsync(() => services.GetRequiredService<ProfileDialogs>().ShowSetupAssistantAsync());
            await WithDialogsClosedAsync(() => ProfileDialogs.ConfirmDeleteAsync("Rig", ruleCount: 1));

            var tray = new Window { Content = services.GetRequiredService<TrayPopupView>(), SizeToContent = SizeToContent.WidthAndHeight };
            Offscreen(tray);
            tray.Show();
            await IdleAsync();
            tray.Content = null;
            tray.Close();
        }

        main.Close();
    }

    /// <summary>A tab builds its content only when it is selected; every tab of the page is selected once.</summary>
    private static async Task EveryTabAsync(DependencyObject root)
    {
        foreach (TabControl tabs in Descendants<TabControl>(root).ToList())
        {
            for (int index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                await IdleAsync();
            }

            tabs.SelectedIndex = 0;
        }
    }

    /// <summary>Opens a dialog the way the app does and closes it once it has shown, modal or not.</summary>
    private static async Task WithDialogsClosedAsync(Func<Task> open)
    {
        Window[] before = [.. Application.Current.Windows.OfType<Window>()];
        int closed = 0;
        var closer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(100) };
        closer.Tick += (_, _) =>
        {
            foreach (Window window in Application.Current.Windows.OfType<Window>().Where(w => w.IsLoaded && !before.Contains(w)).ToList())
            {
                window.Close();
                closed++;
            }
        };
        closer.Start();
        try
        {
            await open().WaitAsync(Settle);
        }
        finally
        {
            closer.Stop();
        }

        closed.ShouldBeGreaterThan(0, "the dialog should have opened");
    }

    /// <summary>Waits until layout, bindings and loaded handlers are done; a layout that never settles fails the test.</summary>
    private static async Task IdleAsync() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(Settle);

    private static void Offscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>The app's container with demo data and nothing that reaches hardware, the registry or the network.</summary>
    private IServiceCollection Services()
    {
        Profile desk = Profile("Desk", DeskModes, confirm: true);
        Profile rig = Rig(confirm: true) with { Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 } };
        var profiles = new InMemoryProfileStore();
        profiles.Profiles.AddRange([desk, rig]);
        var games = new InMemoryGameStore();
        games.Games.Add(new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = "iRacing",
            Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
            ProfileId = rig.Id,
        });

        IAudioController audio = Substitute.For<IAudioController>();
        audio.ListAsync(default, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<AudioDeviceInfo>>([]));
        IUsbDeviceList usb = Substitute.For<IUsbDeviceList>();
        usb.ConnectedDevices().Returns([]);
        usb.PresentDeviceIds().Returns(new HashSet<string>());

        return new ServiceCollection()
            .AddRigShift(_paths, Substitute.For<IAppShell>(), Logger.None)
            .Replace(ServiceDescriptor.Singleton<IProfileStore>(profiles))
            .Replace(ServiceDescriptor.Singleton<IGameStore>(games))
            .Replace(ServiceDescriptor.Singleton<IDisplayConfigurator>(new FakeDisplayConfigurator(DeskActive())))
            .Replace(ServiceDescriptor.Singleton(audio))
            .Replace(ServiceDescriptor.Singleton(usb))
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
    }

    /// <summary>WPF writes binding failures to a trace source that is silent unless someone listens.</summary>
    private sealed class BindingErrors : TraceListener
    {
        private readonly System.Text.StringBuilder _line = new();

        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        }

        public ConcurrentQueue<string> Lines { get; } = new();

        public override void Write(string? message) => _line.Append(message);

        public override void WriteLine(string? message)
        {
            Lines.Enqueue(_line.Append(message).ToString());
            _line.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }

    /// <summary>The app's language is static; the other tests expect it back as it was.</summary>
    private sealed record CultureSnapshot(System.Globalization.CultureInfo UiCulture, System.Globalization.CultureInfo? DefaultUiCulture)
    {
        public static CultureSnapshot Take() => new(Loc.Instance.UICulture, System.Globalization.CultureInfo.DefaultThreadCurrentUICulture);

        public void Restore()
        {
            Loc.Instance.SetLanguage(UiCulture.Name);
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = DefaultUiCulture;
        }
    }
}
