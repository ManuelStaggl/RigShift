using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Services;
using RigShift.Avalonia.Tray;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using Serilog;

namespace RigShift.Avalonia;

/// <summary>
/// The tray app's container: the shared services and what Avalonia does. A test builds it with <c>ValidateOnBuild</c>,
/// so a missing registration fails there instead of when the user opens a page.
/// </summary>
internal static class ServiceRegistration
{
    public static IServiceCollection AddRigShiftApp(this IServiceCollection services, AppPaths paths, IAppShell shell, ILogger log)
    {
        services.AddRigShiftServices(paths, shell, log);
        services.AddSingleton<AvaloniaSwitchConfirmation>();
        services.AddSingleton<ISwitchConfirmation>(sp => sp.GetRequiredService<AvaloniaSwitchConfirmation>());
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<DisplayChangeWatcher>();
        services.AddSingleton<ITextClipboard, AvaloniaClipboard>();
        services.AddSingleton<IAppPicker, Views.AvaloniaAppPicker>();
        services.AddSingleton<ProfileEditorFactory>();
        services.AddSingleton<PageDialogs>();
        services.AddSingleton<IProfilePageDialogs>(sp => sp.GetRequiredService<PageDialogs>());
        services.AddSingleton<IHelpPageDialogs>(sp => sp.GetRequiredService<PageDialogs>());
        services.AddSingleton<GameEditorFactory>();
        services.AddSingleton<GameDialogs>();
        services.AddSingleton<IGamePageDialogs>(sp => sp.GetRequiredService<GameDialogs>());
        services.AddSingleton(sp => new HotkeyService(
            sp.GetRequiredService<ProfileCatalog>(),
            sp.GetRequiredService<GameCatalog>(),
            sp.GetRequiredService<GameSessionService>(),
            sp.GetRequiredService<SwitchCoordinator>(),
            sp.GetRequiredService<SettingsService>(),
            log,
            new HotkeyRegistrar(),
            sp.GetRequiredService<AvaloniaSwitchConfirmation>()));

        // Pipe requests run on the UI thread, and their awaits stay there.
        services.AddSingleton(sp => new CommandPipeServer(
            sp.GetRequiredService<CommandRunner>(), shell, log, work => Dispatcher.UIThread.InvokeAsync(work)));
        return services;
    }
}
