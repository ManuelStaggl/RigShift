using RigShift.App.Services;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>The app's services a game editor works with: the same for every editor, so the container builds them once.</summary>
public sealed record GameEditorServices(
    GameCatalog Catalog,
    HotkeyService Hotkeys,
    IAppPicker AppPicker,
    ILogger Log);
