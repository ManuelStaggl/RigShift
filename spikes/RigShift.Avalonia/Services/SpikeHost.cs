using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>
/// The services of both spike apps (Semi and ShadUI), wired like the WinUI app (src/RigShift.UI/App.xaml.cs) and loaded
/// in its order: settings, profiles, games. Left out on purpose: tray, command pipe, USB automation, game watching,
/// updates – the spike shows pages and runs nothing.
/// </summary>
internal static class SpikeHost
{
    public static async Task<ServiceProvider> StartAsync(AppPaths paths, IAppShell shell)
    {
        ServiceProvider provider = Register(new ServiceCollection(), paths, shell).BuildServiceProvider();
        await provider.GetRequiredService<SettingsService>().LoadAsync(CancellationToken.None);
        await provider.GetRequiredService<ProfileCatalog>().ReloadAsync(CancellationToken.None);
        await provider.GetRequiredService<GameCatalog>().ReloadAsync(CancellationToken.None);
        PreviewActive(provider);
        return provider;
    }

    /// <summary>RIGSHIFT_PREVIEW_ACTIVE=&lt;profile&gt; lights that profile if the preview displays did not already.</summary>
    private static void PreviewActive(ServiceProvider provider)
    {
        string? name = Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_ACTIVE");
        ProfileCatalog catalog = provider.GetRequiredService<ProfileCatalog>();
        if (name is not null && !catalog.Items.Any(i => i.IsActive) && catalog.Items.FirstOrDefault(i => i.Name == name) is { } item)
        {
            item.IsActive = true;
            provider.GetRequiredService<OverviewViewModel>().ActiveName = item.Name;
        }
    }

    private static ServiceCollection Register(ServiceCollection services, AppPaths paths, IAppShell shell)
    {
        services.AddRigShiftServices(paths, shell, Log.Logger);

        // Last registration wins: displays the spike can read but never change.
        string? active = Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_ACTIVE");
        services.AddSingleton<IDisplayConfigurator>(sp => new PreviewDisplays(sp.GetRequiredService<IProfileStore>(), active));
        services.AddSingleton<ISwitchConfirmation, KeepSwitchConfirmation>();
        services.AddSingleton<ITextClipboard, LogClipboard>();
        services.AddSingleton<IAppPicker, NoAppPicker>();
        services.AddSingleton<ProfileEditorFactory>();
        services.AddSingleton<SpikeDialogs>();
        services.AddSingleton<IProfilePageDialogs>(sp => sp.GetRequiredService<SpikeDialogs>());
        services.AddSingleton<IHelpPageDialogs>(sp => sp.GetRequiredService<SpikeDialogs>());
        services.AddSingleton(sp => new HotkeyService(
            sp.GetRequiredService<ProfileCatalog>(),
            sp.GetRequiredService<GameCatalog>(),
            sp.GetRequiredService<GameSessionService>(),
            sp.GetRequiredService<SwitchCoordinator>(),
            sp.GetRequiredService<SettingsService>(),
            Log.Logger,
            new NoHotkeys()));
        SpikeIcons.Install();
        return services;
    }
}
