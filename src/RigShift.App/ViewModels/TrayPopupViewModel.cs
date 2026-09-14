using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;

namespace RigShift.App.ViewModels;

public sealed partial class TrayPopupViewModel : ObservableObject
{
    private readonly IAppShell _shell;

    public TrayPopupViewModel(ProfileCatalog catalog, SwitchCoordinator coordinator, IAppShell shell)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        Coordinator = coordinator;
        _shell = shell;
        catalog.Changed += (_, _) => OnStatusChanged();
        Loc.Instance.PropertyChanged += (_, _) => OnStatusChanged();
    }

    public event EventHandler? CloseRequested;

    public ProfileCatalog Catalog { get; }

    public SwitchCoordinator Coordinator { get; }

    /// <summary>Shown below the header only when no profile row is marked active.</summary>
    public string? StatusText => Catalog.IsEmpty ? Loc.Instance["Tray_NoProfiles"]
        : !Catalog.Items.Any(i => i.IsActive) ? Loc.Instance["Tray_ActiveNone"]
        : null;

    public bool HasStatusText => StatusText is not null;

    [RelayCommand]
    private async Task SwitchAsync(ProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        await Coordinator.SwitchAsync(item.Profile);
    }

    [RelayCommand]
    private void Open()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _shell.ShowMainWindow();
    }

    [RelayCommand]
    private void Settings()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _shell.ShowMainWindow(typeof(Views.Pages.SettingsPage));
    }

    [RelayCommand]
    private void Exit() => _shell.Quit();

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatusText));
    }
}
