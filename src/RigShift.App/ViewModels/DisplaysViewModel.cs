using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>The monitors attached right now, with their custom names and a way to tell them apart (docs/PLAN.md, section 6).</summary>
public sealed partial class DisplaysViewModel(IDisplayConfigurator display, ProfileCatalog catalog, ILogger log) : ObservableObject
{
    private readonly ILogger _log = log.ForContext<DisplaysViewModel>();

    public ObservableCollection<DisplayCard> Displays { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    internal async Task RenameAsync(DisplayCard card, string? name)
    {
        try
        {
            await catalog.RenameDisplayAsync(card.TargetDevicePath, name, CancellationToken.None);
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Display {Display} could not be renamed", card.ModelName);
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            DisplaySnapshot snapshot = await Task.Run(() => display.QueryAsync(CancellationToken.None));
            IReadOnlyDictionary<string, string> names = catalog.KnownDisplayNames;
            Displays.Clear();
            IReadOnlyList<(AttachedDisplay Display, int? Number)> numbered = DisplayNumbers.Assign(snapshot.Displays);
            foreach ((AttachedDisplay attached, int? shown) in numbered)
            {
                Displays.Add(new DisplayCard(this, attached, shown, names.GetValueOrDefault(attached.Identity.TargetDevicePath), ProfilesWith(attached)));
            }

            _log.Information("Display numbers: {Numbers}", string.Join(", ", numbered
                .Where(n => n.Number is not null)
                .Select(n => $"{n.Number} = {n.Display.Identity.FriendlyName} (Windows {n.Display.WindowsNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"})")));

            IsEmpty = Displays.Count == 0;
            ErrorMessage = null;
            _log.Information("Displays page shows {Count} displays", Displays.Count);
        }
        catch (Win32Exception ex)
        {
            _log.Error(ex, "Displays could not be read");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Identify()
    {
        var shown = Displays
            .Where(d => d.Number is not null && d.Mode is not null)
            .Select(d => (d.Number!.Value, d.Name, d.Mode!))
            .ToList();
        _log.Information("Identifying {Count} displays", shown.Count);
        Views.IdentifyWindow.ShowAll(shown);
    }

    private string ProfilesWith(AttachedDisplay attached)
    {
        List<string> names = catalog.Profiles
            .Where(p => p.Displays.Any(d => string.Equals(d.Identity.TargetDevicePath, attached.Identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();
        return names.Count == 0 ? Loc.Instance["Displays_NotInProfiles"] : Loc.Format("Displays_InProfiles", string.Join(", ", names));
    }
}
