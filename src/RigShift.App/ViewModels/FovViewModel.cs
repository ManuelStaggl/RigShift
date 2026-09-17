using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Fov;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The field-of-view dialog: picks a display, takes its picture size from the EDID, asks for the eye distance and
/// shows the value each sim wants. Distance, layout and bezel are remembered in the settings; the size is not – it
/// comes from the display every time.
/// </summary>
public sealed partial class FovViewModel : ObservableObject
{
    public const int DefaultDistanceCm = 60;
    public const int DefaultBezelMm = 10;

    private readonly IDisplaySizeReader _sizes;
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private readonly Dictionary<string, ScreenSize?> _measured = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public FovViewModel(IReadOnlyList<AttachedDisplay> displays, IReadOnlyDictionary<string, string> names, IDisplaySizeReader sizes, SettingsService settings, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        _sizes = sizes;
        _settings = settings;
        _log = log.ForContext<FovViewModel>();

        AppSettings current = settings.Current;
        _loading = true;
        DistanceCm = current.FovDistanceCm ?? DefaultDistanceCm;
        BezelMm = current.FovBezelMm ?? DefaultBezelMm;
        IsTriple = current.FovTriple;

        foreach (AttachedDisplay display in displays.Where(d => d.IsActive && d.ActiveMode is not null))
        {
            Displays.Add(new FovDisplay(display, SwitchMessages.NameOf(names.GetValueOrDefault(display.Identity.TargetDevicePath), display.Identity)));
        }

        SelectedDisplay = Displays.FirstOrDefault(d => d.Mode.IsPrimary) ?? Displays.FirstOrDefault();
        _loading = false;
        LoadSize();
    }

    public ObservableCollection<FovDisplay> Displays { get; } = [];

    public ObservableCollection<FovGameRow> GameRows { get; } = [];

    public bool HasDisplays => Displays.Count > 0;

    [ObservableProperty]
    public partial FovDisplay? SelectedDisplay { get; set; }

    /// <summary>Diagonal in inches, editable; the aspect ratio stays the one of the EDID or the resolution.</summary>
    [ObservableProperty]
    public partial double DiagonalInches { get; set; }

    [ObservableProperty]
    public partial double DistanceCm { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingle))]
    public partial bool IsTriple { get; set; }

    /// <summary>The other radio button; both bind to one flag.</summary>
    public bool IsSingle
    {
        get => !IsTriple;
        set => IsTriple = !value;
    }

    [ObservableProperty]
    public partial double BezelMm { get; set; }

    /// <summary>"59.8 × 33.6 cm, from the display" or the hint that the size was typed.</summary>
    [ObservableProperty]
    public partial string SizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SizeIsMeasured { get; set; }

    [ObservableProperty]
    public partial string VerticalText { get; set; } = "—";

    [ObservableProperty]
    public partial string HorizontalText { get; set; } = "—";

    [ObservableProperty]
    public partial string TripleAngleText { get; set; } = "—";

    [ObservableProperty]
    public partial string TripleTotalText { get; set; } = "—";

    [ObservableProperty]
    public partial string? CopyStatus { get; set; }

    /// <summary>The picture used for the numbers: the display's aspect ratio at the diagonal in the field.</summary>
    public ScreenSize CurrentScreen { get; private set; } = new(0, 0);

    partial void OnSelectedDisplayChanged(FovDisplay? value)
    {
        if (!_loading)
        {
            LoadSize();
        }
    }

    partial void OnDiagonalInchesChanged(double value)
    {
        if (!_loading)
        {
            CurrentScreen = AspectOf(SelectedDisplay).WithDiagonal(value);
            SizeIsMeasured = false;
            UpdateSizeText();
            Recalculate();
        }
    }

    partial void OnDistanceCmChanged(double value) => OnInputChanged();

    partial void OnIsTripleChanged(bool value) => OnInputChanged();

    partial void OnBezelMmChanged(double value) => OnInputChanged();

    [RelayCommand]
    private void Copy(FovGameRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(row.ValueText);
            CopyStatus = Loc.Format("Fov_Copied", row.Game);
            _log.Information("FOV value {Value} for {Game} copied", row.ValueText, row.Game);
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "FOV value could not be copied");
            CopyStatus = Loc.Format("About_CopyFailed", ex.Message);
        }
    }

    /// <summary>Remembers distance, layout and bezel; called when the dialog closes.</summary>
    public Task SaveInputsAsync(CancellationToken cancellationToken)
    {
        int distance = (int)Math.Round(DistanceCm);
        int bezel = (int)Math.Round(BezelMm);
        bool triple = IsTriple;
        return _settings.UpdateAsync(s => s with { FovDistanceCm = distance, FovBezelMm = bezel, FovTriple = triple }, cancellationToken, notify: false);
    }

    private void OnInputChanged()
    {
        if (!_loading)
        {
            Recalculate();
        }
    }

    private void LoadSize()
    {
        _loading = true;
        try
        {
            ScreenSize? measured = SelectedDisplay is { } display ? Measure(display) : null;
            SizeIsMeasured = measured is not null;
            CurrentScreen = measured ?? AspectOf(SelectedDisplay).WithDiagonal(DiagonalInches > 0 ? DiagonalInches : 27);
            DiagonalInches = Math.Round(CurrentScreen.DiagonalInches, 1);
            UpdateSizeText();
        }
        finally
        {
            _loading = false;
        }

        Recalculate();
    }

    private ScreenSize? Measure(FovDisplay display)
    {
        string key = display.Identity.TargetDevicePath;
        if (!_measured.TryGetValue(key, out ScreenSize? size))
        {
            size = _sizes.Read(display.Identity);
            _measured[key] = size is { IsKnown: true } ? size : null;
            size = _measured[key];
        }

        return size;
    }

    /// <summary>The aspect ratio from the EDID if it has one, otherwise from the resolution – both give the same on a normal panel.</summary>
    private ScreenSize AspectOf(FovDisplay? display)
    {
        if (display is null)
        {
            return new ScreenSize(16, 9);
        }

        if (_measured.TryGetValue(display.Identity.TargetDevicePath, out ScreenSize? measured) && measured is not null)
        {
            return measured;
        }

        return new ScreenSize(display.Mode.Width, display.Mode.Height);
    }

    private void UpdateSizeText()
    {
        string size = string.Create(Loc.Instance.Culture, $"{CurrentScreen.WidthMm / 10:0.#} × {CurrentScreen.HeightMm / 10:0.#} cm");
        SizeText = SizeIsMeasured ? Loc.Format("Fov_SizeMeasured", size) : Loc.Format("Fov_SizeTyped", size);
    }

    private void Recalculate()
    {
        FovResult result = FovCalculator.Calculate(new FovInput(CurrentScreen, DistanceCm * 10, IsTriple, BezelMm));
        VerticalText = Degrees(result.VerticalDegrees);
        HorizontalText = Degrees(result.HorizontalDegrees);
        TripleAngleText = result.TripleAngleDegrees is { } angle ? Degrees(angle) : "—";
        TripleTotalText = result.TripleHorizontalDegrees is { } total ? Degrees(total) : "—";

        GameRows.Clear();
        foreach (GameFov game in FovCalculator.ForGames(result))
        {
            GameRows.Add(new FovGameRow(game));
        }

        CopyStatus = null;
    }

    private static string Degrees(double value) => value > 0 ? value.ToString("0.0", Loc.Instance.Culture) + "°" : "—";
}

/// <summary>One active display in the dialog's list.</summary>
public sealed class FovDisplay(AttachedDisplay display, string name)
{
    public DisplayIdentity Identity { get; } = display.Identity;

    public DisplayAssignment Mode { get; } = display.ActiveMode!;

    public string Name { get; } = name;

    public override string ToString() => Name;
}

/// <summary>One line of the table: the sim, what kind of angle it wants and the number to type in.</summary>
public sealed class FovGameRow(GameFov fov)
{
    public string Game { get; } = fov.Game;

    public string KindText { get; } = Loc.Instance["Fov_Kind_" + fov.Kind];

    /// <summary>Whole degrees for the sims' sliders; the calculators round the same way.</summary>
    public string ValueText { get; } = fov.Degrees > 0 ? Math.Round(fov.Degrees).ToString("0", Loc.Instance.Culture) : "—";
}
