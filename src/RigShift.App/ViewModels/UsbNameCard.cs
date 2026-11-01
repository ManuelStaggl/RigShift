using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.Core.Automation;

namespace RigShift.App.ViewModels;

/// <summary>
/// One USB device in the settings' device table. The name is saved when the field loses focus or on Enter, like a
/// display name (F6), because no hardware state depends on it.
/// </summary>
public sealed partial class UsbNameCard : ObservableObject
{
    private readonly UsbDevicesViewModel _owner;
    private string? _savedName;

    public UsbNameCard(UsbDevicesViewModel owner, string id, string windowsName, bool isConnected, string? customName)
    {
        _owner = owner;
        Id = id;
        WindowsName = windowsName;
        _savedName = UsbDeviceNames.Normalize(customName);
        CustomName = _savedName ?? string.Empty;
        StateKind = isConnected ? StatusKind.Ok : StatusKind.Neutral;
        StateText = Loc.Instance[isConnected ? "Automation_NameConnected" : "Automation_NameNotConnected"];
    }

    /// <summary>Vendor and product id (<c>VID_046D&amp;PID_C24F</c>); shown so two devices of a kind stay apart.</summary>
    public string Id { get; }

    public string WindowsName { get; }

    public StatusKind StateKind { get; }

    public string StateText { get; }

    [ObservableProperty]
    public partial string CustomName { get; set; }

    /// <summary>The name on disk; Esc puts it back into the field.</summary>
    internal string? SavedName => _savedName;

    internal async Task SaveNameAsync()
    {
        string? name = UsbDeviceNames.Normalize(CustomName);
        if (name == _savedName)
        {
            return;
        }

        _savedName = name;
        await _owner.RenameAsync(this, name);
    }
}
