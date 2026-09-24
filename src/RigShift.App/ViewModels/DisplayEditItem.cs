using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.App.ViewModels;

public sealed record RefreshChoice(RefreshRate Rate)
{
    public string Text => Rate.Hertz.ToString("0.##", Loc.Instance.Culture) + " Hz";
}

public sealed record HdrChoice(bool? Value, string Text);

/// <summary>One display of the profile: chosen in the picture, edited in the card under it.</summary>
public sealed partial class DisplayEditItem : ObservableObject
{
    private readonly ProfileEditorViewModel _owner;
    private bool _syncing;

    public DisplayEditItem(ProfileEditorViewModel owner, DisplayAssignment assignment)
    {
        _owner = owner;
        Assignment = assignment;
        CustomName = assignment.CustomName ?? string.Empty;
        Sync(assignment);
    }

    public DisplayAssignment Assignment { get; private set; }

    /// <summary>The device path; what the picture reports as the selected key.</summary>
    public string Key => Assignment.Identity.TargetDevicePath;

    /// <summary>"Name · Model", as the switch messages call it.</summary>
    public string Name => SwitchMessages.NameOf(Assignment);

    /// <summary>The monitor as Windows calls it, under the name in the card.</summary>
    public string ModelName => DisplayNames.Of(Assignment.Identity);

    [ObservableProperty]
    public partial string ModeText { get; private set; } = string.Empty;

    /// <summary>"3840 × 2160", under the model name in the card; the position is in the picture.</summary>
    public string ResolutionText => string.Create(Loc.Instance.Culture, $"{Assignment.Width} × {Assignment.Height}");

    /// <summary>The user's name for this monitor; saved with the profile and carried to every profile with the same monitor.</summary>
    [ObservableProperty]
    public partial string CustomName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeOptional))]
    public partial bool IsPrimary { get; set; }

    [ObservableProperty]
    public partial bool IsOptional { get; set; }

    public bool CanBeOptional => !IsPrimary;

    public ObservableCollection<RefreshChoice> RefreshChoices { get; } = [];

    [ObservableProperty]
    public partial RefreshChoice? SelectedRefresh { get; set; }

    public ObservableCollection<HdrChoice> HdrChoices { get; } = [.. NewHdrChoices()];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwitchesHdr))]
    public partial HdrChoice? SelectedHdr { get; set; }

    /// <summary>HDR is set on or off: the editor warns to try it in Windows first (finding HW-12).</summary>
    public bool SwitchesHdr => SelectedHdr?.Value is not null;

    /// <summary>The display offered no rates now and none are remembered, so only the saved one is listed (HW-13).</summary>
    [ObservableProperty]
    public partial bool RatesUnknown { get; private set; }

    /// <summary>New texts after a language change; <see cref="Sync"/> selects the same values again.</summary>
    internal void Relabel()
    {
        _syncing = true;
        try
        {
            HdrChoices.Clear();
            foreach (HdrChoice choice in NewHdrChoices())
            {
                HdrChoices.Add(choice);
            }
        }
        finally
        {
            _syncing = false;
        }

        Sync(Assignment);
        OnPropertyChanged(nameof(Name));
    }

    private static HdrChoice[] NewHdrChoices() =>
    [
        new(null, Loc.Instance["Hdr_Unchanged"]),
        new(true, Loc.Instance["Hdr_On"]),
        new(false, Loc.Instance["Hdr_Off"]),
    ];

    internal void Sync(DisplayAssignment assignment)
    {
        _syncing = true;
        try
        {
            Assignment = assignment;
            IsPrimary = assignment.IsPrimary;
            IsOptional = assignment.IsOptional;
            ModeText = Loc.Format("Editor_Mode", assignment.Width, assignment.Height, assignment.PositionX, assignment.PositionY);

            RefreshRate rate = RefreshRate.Of(assignment);
            if (!RefreshChoices.Any(c => c.Rate == rate))
            {
                RefreshChoices.Add(new RefreshChoice(rate));
            }

            SelectedRefresh = RefreshChoices.First(c => c.Rate == rate);
            SelectedHdr = HdrChoices.First(c => c.Value == assignment.Hdr);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Adds the rates the display offers. The saved rate stays, also when the list has one that looks the same.</summary>
    internal void OfferRefreshRates(IReadOnlyList<RefreshRate> rates)
    {
        RefreshRate current = RefreshRate.Of(Assignment);
        List<RefreshRate> all = [current, .. rates.Where(r => !r.LooksLike(current))];
        RatesUnknown = rates.Count == 0;
        _syncing = true;
        try
        {
            RefreshChoices.Clear();
            foreach (RefreshRate rate in all.OrderByDescending(r => r.Hertz))
            {
                RefreshChoices.Add(new RefreshChoice(rate));
            }

            SelectedRefresh = RefreshChoices.First(c => c.Rate == current);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnCustomNameChanged(string value)
    {
        if (!_syncing)
        {
            Assignment = Assignment with { CustomName = DisplayNames.Normalize(value) };
            OnPropertyChanged(nameof(Name));
            _owner.OnDisplaysEdited();
        }
    }

    partial void OnSelectedRefreshChanged(RefreshChoice? value)
    {
        if (!_syncing && value is not null)
        {
            Assignment = Assignment with { RefreshNumerator = value.Rate.Numerator, RefreshDenominator = value.Rate.Denominator };
            _owner.OnDisplaysEdited();
        }
    }

    partial void OnSelectedHdrChanged(HdrChoice? value)
    {
        if (!_syncing && value is not null)
        {
            Assignment = Assignment with { Hdr = value.Value };
            _owner.OnDisplaysEdited();
        }
    }

    partial void OnIsPrimaryChanged(bool value)
    {
        if (!_syncing && value)
        {
            _owner.MakePrimary(this);
        }
    }

    partial void OnIsOptionalChanged(bool value)
    {
        if (!_syncing)
        {
            Assignment = Assignment with { IsOptional = value };
            _owner.OnDisplaysEdited();
        }
    }
}
