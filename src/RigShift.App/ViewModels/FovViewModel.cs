using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
/// The field-of-view page: picks a display, takes its picture size from the EDID, asks for the curve, the eye
/// distance and how the rig stands, and shows the angles plus the value every sim wants. Everything the user types
/// is remembered; the size is not – it comes from the display every time.
/// </summary>
public sealed partial class FovViewModel : ObservableObject
{
    public const int DefaultDistanceCm = 60;
    public const int DefaultBezelMm = 10;
    public const int DefaultDiagonalInches = 27;
    private const string CustomCurvature = "custom";

    private readonly IDisplayConfigurator _display;
    private readonly ProfileCatalog _catalog;
    private readonly IDisplaySizeReader _sizes;
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private readonly Dictionary<string, ScreenSize?> _measured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _curvature = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private bool _loading;

    public FovViewModel(
        IDisplayConfigurator display,
        ProfileCatalog catalog,
        IDisplaySizeReader sizes,
        SettingsService settings,
        DisplayChangeWatcher? watcher,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        _display = display;
        _catalog = catalog;
        _sizes = sizes;
        _settings = settings;
        _log = log.ForContext<FovViewModel>();

        AppSettings current = settings.Current;
        _loading = true;
        DistanceCm = current.FovDistanceCm ?? DefaultDistanceCm;
        BezelMm = current.FovBezelMm ?? DefaultBezelMm;
        IsTriple = current.FovTriple;
        Comfort = current.FovComfort;
        VerticalOffsetCm = current.FovVerticalOffsetCm ?? 0;
        AngleIsAutomatic = current.FovAngleDegrees is null;
        AngleDegrees = current.FovAngleDegrees ?? 0;
        foreach ((string key, int radius) in current.FovCurvatureMm ?? new Dictionary<string, int>())
        {
            _curvature[key] = radius;
        }

        FillCurvatureChoices();
        _loading = false;

        // Null only in tests: the watcher owns a window handle and needs a running WPF app.
        if (watcher is not null)
        {
            watcher.DisplaysChanged += (_, _) => OnUi(() => RefreshCommand.Execute(null));
        }

        Loc.Instance.PropertyChanged += (_, _) => OnUi(() =>
        {
            FillCurvatureChoices();
            UpdateSizeText();
            Recalculate();
        });
    }

    public ObservableCollection<FovDisplay> Displays { get; } = [];

    public ObservableCollection<Choice> CurvatureChoices { get; } = [];

    public ObservableCollection<FovSimRow> SimRows { get; } = [];

    /// <summary>What the numbers cannot be trusted with, in the user's words.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasDisplays => Displays.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial bool HasNoWarnings { get; set; } = true;

    public bool HasWarnings => !HasNoWarnings;

    [ObservableProperty]
    public partial FovDisplay? SelectedDisplay { get; set; }

    /// <summary>
    /// Diagonal in inches, editable; the aspect ratio stays the one of the EDID or the resolution. It starts at a
    /// usual panel size because the field would otherwise clamp an empty value to its own minimum and keep it.
    /// </summary>
    [ObservableProperty]
    public partial double DiagonalInches { get; set; } = DefaultDiagonalInches;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomCurvature))]
    public partial Choice? SelectedCurvature { get; set; }

    public bool IsCustomCurvature => SelectedCurvature?.Key == CustomCurvature;

    [ObservableProperty]
    public partial double CustomRadiusMm { get; set; } = 1000;

    /// <summary>The side angle comes from the geometry; switching this off lets the user enter what the rig really is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AngleIsManual))]
    public partial bool AngleIsAutomatic { get; set; } = true;

    public bool AngleIsManual => !AngleIsAutomatic;

    [ObservableProperty]
    public partial double AngleDegrees { get; set; }

    [ObservableProperty]
    public partial bool Comfort { get; set; }

    /// <summary>How far the eye sits above the middle of the picture; changes no angle, only the hint.</summary>
    [ObservableProperty]
    public partial double VerticalOffsetCm { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SizeIsMeasured { get; set; }

    [ObservableProperty]
    public partial string VerticalText { get; set; } = "—";

    [ObservableProperty]
    public partial string HorizontalText { get; set; } = "—";

    /// <summary>What a curved panel really covers; empty on a flat one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrueHorizontal))]
    public partial string? TrueHorizontalText { get; set; }

    public bool HasTrueHorizontal => TrueHorizontalText is not null;

    [ObservableProperty]
    public partial string AngleText { get; set; } = "—";

    [ObservableProperty]
    public partial string TotalText { get; set; } = "—";

    [ObservableProperty]
    public partial string PixelDensityText { get; set; } = "—";

    [ObservableProperty]
    public partial string OuterEdgeText { get; set; } = "—";

    /// <summary>"60 cm · 1800R · three screens" under the picture, and the screen reader's text for it.</summary>
    [ObservableProperty]
    public partial string DiagramText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? VerticalOffsetHint { get; set; }

    [ObservableProperty]
    public partial string? CopyStatus { get; set; }

    /// <summary>What the page draws and calculates with – the diagram animates from one of these to the next.</summary>
    [ObservableProperty]
    public partial FovInput Input { get; set; } = new() { Screen = new ScreenSize(0, 0), DistanceMm = 0 };

    /// <summary>The picture used for the numbers: the display's aspect ratio at the diagonal in the field.</summary>
    public ScreenSize CurrentScreen { get; private set; } = new(0, 0);

    /// <summary>Reads the attached displays; the page calls it when it is shown and the watcher when they change.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        string? selected = SelectedDisplay?.Identity.TargetDevicePath;
        try
        {
            DisplaySnapshot snapshot = await Task.Run(() => _display.QueryAsync(CancellationToken.None));
            IReadOnlyDictionary<string, string> names = _catalog.KnownDisplayNames;
            _loading = true;
            Displays.Clear();
            foreach (AttachedDisplay display in snapshot.Displays.Where(d => d.IsActive && d.ActiveMode is not null))
            {
                Displays.Add(new FovDisplay(display, SwitchMessages.NameOf(names.GetValueOrDefault(display.Identity.TargetDevicePath), display.Identity)));
            }

            OnPropertyChanged(nameof(HasDisplays));
            SelectedDisplay = Displays.FirstOrDefault(d => d.Identity.TargetDevicePath == selected)
                ?? Displays.FirstOrDefault(d => d.Mode.IsPrimary)
                ?? Displays.FirstOrDefault();
            _loading = false;
            LoadDisplay();
            _log.Information("Field of view page shows {Count} active displays", Displays.Count);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _loading = false;
            _log.Error(ex, "Displays could not be read for the field of view page");
        }
    }

    partial void OnSelectedDisplayChanged(FovDisplay? value)
    {
        if (!_loading)
        {
            LoadDisplay();
        }
    }

    partial void OnDiagonalInchesChanged(double value)
    {
        if (_loading)
        {
            return;
        }

        CurrentScreen = AspectOf(SelectedDisplay).WithDiagonal(value);
        SizeIsMeasured = false;
        UpdateSizeText();
        Recalculate();
    }

    partial void OnDistanceCmChanged(double value) => OnInputChanged();

    partial void OnIsTripleChanged(bool value) => OnInputChanged();

    partial void OnBezelMmChanged(double value) => OnInputChanged();

    partial void OnComfortChanged(bool value) => OnInputChanged();

    partial void OnVerticalOffsetCmChanged(double value) => OnInputChanged();

    partial void OnAngleDegreesChanged(double value)
    {
        if (!AngleIsAutomatic)
        {
            OnInputChanged();
        }
    }

    partial void OnAngleIsAutomaticChanged(bool value) => OnInputChanged();

    partial void OnCustomRadiusMmChanged(double value)
    {
        if (IsCustomCurvature)
        {
            OnCurvatureChanged();
        }
    }

    partial void OnSelectedCurvatureChanged(Choice? value) => OnCurvatureChanged();

    [RelayCommand]
    private void Copy(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(value);
            CopyStatus = Loc.Format("Fov_Copied", value);
            _log.Information("Field of view value {Value} copied", value);
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "Field of view value could not be copied");
            CopyStatus = Loc.Format("About_CopyFailed", ex.Message);
        }
    }

    /// <summary>The curve is remembered per display: a rig usually mixes a curved centre with flat sides.</summary>
    private void OnCurvatureChanged()
    {
        if (_loading)
        {
            return;
        }

        if (SelectedDisplay is { } display)
        {
            int radius = CurrentRadiusMm;
            if (radius > 0)
            {
                _curvature[display.Identity.TargetDevicePath] = radius;
            }
            else
            {
                _curvature.Remove(display.Identity.TargetDevicePath);
            }
        }

        Recalculate();
    }

    private int CurrentRadiusMm => SelectedCurvature?.Key switch
    {
        null or "0" => 0,
        CustomCurvature => (int)Math.Round(CustomRadiusMm),
        string key when int.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out int radius) => radius,
        _ => 0,
    };

    private void OnInputChanged()
    {
        if (!_loading)
        {
            Recalculate();
        }
    }

    private void FillCurvatureChoices()
    {
        string? selected = SelectedCurvature?.Key;
        CurvatureChoices.Clear();
        CurvatureChoices.Add(new Choice("0", Loc.Instance["Fov_Flat"]));
        foreach (int radius in DisplayModels.CommonRadii)
        {
            CurvatureChoices.Add(new Choice(radius.ToString(System.Globalization.CultureInfo.InvariantCulture), Loc.Format("Fov_Radius", radius)));
        }

        CurvatureChoices.Add(new Choice(CustomCurvature, Loc.Instance["Fov_RadiusOwn"]));
        SelectedCurvature = CurvatureChoices.FirstOrDefault(c => c.Key == selected) ?? CurvatureChoices[0];
    }

    /// <summary>Size and curve of the display that was just picked; a remembered radius wins over the model table.</summary>
    private void LoadDisplay()
    {
        _loading = true;
        try
        {
            ScreenSize? measured = SelectedDisplay is { } display ? Measure(display) : null;
            SizeIsMeasured = measured is not null;
            CurrentScreen = measured ?? AspectOf(SelectedDisplay).WithDiagonal(DiagonalInches > 0 ? DiagonalInches : DefaultDiagonalInches);
            DiagonalInches = Math.Round(CurrentScreen.DiagonalInches, 1);
            UpdateSizeText();

            int? radius = SelectedDisplay is { } picked
                ? _curvature.TryGetValue(picked.Identity.TargetDevicePath, out int known) ? known : DisplayModels.RadiusFor(picked.Identity.FriendlyName)
                : null;
            string key = radius is null or 0
                ? "0"
                : CurvatureChoices.Any(c => c.Key == radius.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    ? radius.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : CustomCurvature;
            if (key == CustomCurvature)
            {
                CustomRadiusMm = radius!.Value;
            }

            SelectedCurvature = CurvatureChoices.First(c => c.Key == key);
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
        int radius = CurrentRadiusMm;
        var input = new FovInput
        {
            Screen = CurrentScreen,
            DistanceMm = DistanceCm * 10,
            Triple = IsTriple,
            BezelMm = BezelMm,
            CurvatureRadiusMm = radius,
            SideAngleDegrees = AngleIsAutomatic || AngleDegrees <= 0 ? null : AngleDegrees,
            PixelWidth = SelectedDisplay?.Mode.Width ?? 0,
            PixelHeight = SelectedDisplay?.Mode.Height ?? 0,
            Comfort = Comfort,
        };

        FovResult result = FovCalculator.Calculate(input);
        Input = input;

        VerticalText = Degrees(result.VerticalDegrees);
        HorizontalText = Degrees(result.HorizontalDegrees);
        TrueHorizontalText = result.IsCurved && result.IsValid ? Degrees(result.TrueHorizontalDegrees) : null;
        AngleText = result.SideAngleDegrees is { } angle ? Degrees(angle) : "—";
        TotalText = result.TotalDegrees is { } total ? Degrees(total) : "—";
        PixelDensityText = result.PixelsPerDegree > 0 ? result.PixelsPerDegree.ToString("0.0", Loc.Instance.Culture) : "—";
        OuterEdgeText = result.OuterEdgeDistanceMm is { } outer
            ? string.Create(Loc.Instance.Culture, $"{outer / 10:0} cm")
            : "—";

        if (AngleIsAutomatic && result.IdealAngleDegrees is { } ideal)
        {
            _loading = true;
            AngleDegrees = Math.Round(ideal, 1);
            _loading = false;
        }

        DiagramText = Loc.Format(
            "Fov_DiagramSummary",
            string.Create(Loc.Instance.Culture, $"{DistanceCm:0}"),
            radius > 0 ? Loc.Format("Fov_Radius", radius) : Loc.Instance["Fov_Flat"],
            IsTriple ? Loc.Instance["Fov_Triple"] : Loc.Instance["Fov_Single"]);
        VerticalOffsetHint = VerticalOffsetCm > 0 ? Loc.Instance["Fov_OffsetHint"] : null;

        UpdateWarnings(result);
        UpdateSims(input, result);
        CopyStatus = null;
        Persist();
    }

    private void UpdateWarnings(FovResult result)
    {
        Warnings.Clear();
        if (result.Warnings.HasFlag(FovWarnings.EyeInsideCurve))
        {
            Warnings.Add(Loc.Instance["Fov_Warn_Curve"]);
        }

        if (result.Warnings.HasFlag(FovWarnings.TripleImpossible))
        {
            Warnings.Add(Loc.Instance["Fov_Warn_Triple"]);
        }

        if (result.Warnings.HasFlag(FovWarnings.SideAngleTooLarge))
        {
            Warnings.Add(Loc.Instance["Fov_Warn_Angle"]);
        }

        if (result.Warnings.HasFlag(FovWarnings.CurvedTriple))
        {
            Warnings.Add(Loc.Instance["Fov_Warn_CurvedTriple"]);
        }

        if (result.Warnings.HasFlag(FovWarnings.LowPixelDensity))
        {
            Warnings.Add(Loc.Format("Fov_Warn_Pixels", result.PixelsPerDegree.ToString("0", Loc.Instance.Culture)));
        }

        HasNoWarnings = Warnings.Count == 0;
    }

    private void UpdateSims(FovInput input, FovResult result)
    {
        var open = SimRows.Where(r => r.IsExpanded).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        SimRows.Clear();
        foreach (SimValue value in SimCatalog.Evaluate(input, result))
        {
            SimRows.Add(new FovSimRow(value, input.Triple) { IsExpanded = open.Contains(value.Id) });
        }
    }

    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        int distance = (int)Math.Round(DistanceCm);
        int bezel = (int)Math.Round(BezelMm);
        bool triple = IsTriple;
        bool comfort = Comfort;
        int? angle = AngleIsAutomatic ? null : (int)Math.Round(AngleDegrees);
        int? offset = VerticalOffsetCm > 0 ? (int)Math.Round(VerticalOffsetCm) : null;
        Dictionary<string, int>? curvature = _curvature.Count > 0 ? new Dictionary<string, int>(_curvature, StringComparer.OrdinalIgnoreCase) : null;
        _ = PersistAsync(s => s with
        {
            FovDistanceCm = distance,
            FovBezelMm = bezel,
            FovTriple = triple,
            FovComfort = comfort,
            FovAngleDegrees = angle,
            FovVerticalOffsetCm = offset,
            FovCurvatureMm = curvature,
        });
    }

    private async Task PersistAsync(Func<AppSettings, AppSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(change, CancellationToken.None, notify: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Field of view settings could not be saved");
        }
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    private static string Degrees(double value) => value > 0 ? value.ToString("0.0", Loc.Instance.Culture) + "°" : "—";
}

/// <summary>One active display in the page's list.</summary>
public sealed class FovDisplay(AttachedDisplay display, string name)
{
    public DisplayIdentity Identity { get; } = display.Identity;

    public DisplayAssignment Mode { get; } = display.ActiveMode!;

    public string Name { get; } = name;

    public override string ToString() => Name;
}

/// <summary>One line of the table: the sim, the number it wants, where it goes and its own geometry fields.</summary>
public sealed partial class FovSimRow : ObservableObject
{
    public FovSimRow(SimValue value, bool triple)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id = value.Id;
        Sim = value.Sim;
        Value = value.Value;
        Alternative = value.Alternative is { } other ? Loc.Format("Fov_Alternative", other) : null;
        KindText = Loc.Instance["Fov_Kind_" + value.Kind];
        WhereText = Loc.Instance["Fov_Where_" + value.Id];
        Confidence = value.Confidence switch
        {
            FovConfidence.Likely => Loc.Instance["Fov_Conf_Likely"],
            FovConfidence.Unclear => Loc.Instance["Fov_Conf_Unclear"],
            _ => null,
        };
        Fields = value.Fields;
        WideHint = triple && !value.NativeTriple ? Loc.Instance["Fov_NoNativeTriple"] : null;
    }

    public string Id { get; }

    public string Sim { get; }

    public string Value { get; }

    /// <summary>A second reading where the sources disagree – shown next to the value, never instead of it.</summary>
    public string? Alternative { get; }

    public string KindText { get; }

    public string WhereText { get; }

    public string? Confidence { get; }

    public bool HasConfidence => Confidence is not null;

    public IReadOnlyList<SimField> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    public string? WideHint { get; }

    public bool HasWideHint => WideHint is not null;

    /// <summary>The row shows where the value goes and, on triples, the sim's own fields.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
