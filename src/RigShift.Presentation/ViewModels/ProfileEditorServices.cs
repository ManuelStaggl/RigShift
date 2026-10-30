using RigShift.App.Services;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>The app's services a profile editor works with: the same for every editor, so the container builds them once.</summary>
public sealed record ProfileEditorServices(
    ProfileCatalog Catalog,
    IDisplayConfigurator Display,
    IDesktopIcons DesktopIcons,
    HotkeyService Hotkeys,
    SettingsService Settings,
    IAppPicker AppPicker,
    ILogger Log);
