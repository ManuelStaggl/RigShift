using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>
/// Surround in the profile editor. There are three answers per profile: leave it alone (the default, and what every
/// profile before 1.9 means), switch it off, or run this grid. Once another profile runs a grid, "leave alone" means off
/// (<see cref="SurroundDefaults"/>) and is not offered. There is no grid editor: a grid is built once in the NVIDIA
/// control panel and taken over from there, because the driver needs a reload to create one and that closes running games.
/// </summary>
public sealed partial class SurroundSection : ObservableObject
{
    private const string Unchanged = "unchanged";
    private const string Off = "off";
    private const string On = "on";

    private readonly SurroundState _state;
    private readonly SurroundGrid? _grid;
    private readonly string? _usedBy;
    private readonly bool _offByDefault;
    private bool _filling;

    /// <param name="saved">The profile's setting; a grid in it wins over the one the driver reports now.</param>
    /// <param name="usedBy">Another profile that switches Surround on, or <c>null</c>.</param>
    public SurroundSection(SurroundState state, SurroundSetting? saved, string? usedBy = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        _state = state;
        _grid = saved?.Grid ?? (state.Grids.Count > 0 ? state.Grids[0] : null);
        _usedBy = usedBy;
        _offByDefault = saved is null && usedBy is not null;

        // Nothing to offer without an NVIDIA driver - unless the profile already carries a setting from another machine.
        IsVisible = state.Availability == SurroundAvailability.Available || saved is not null;
        if (IsVisible)
        {
            FillChoices(saved is null ? (_offByDefault ? Off : Unchanged) : saved.Enabled ? On : Off);
        }
    }

    /// <summary>The user chose another answer.</summary>
    public event EventHandler? Changed;

    /// <summary>False hides the whole section: a machine without an NVIDIA card has nothing to say here.</summary>
    public bool IsVisible { get; }

    /// <summary>"Leave alone", "off" and "on"; empty while the section is hidden.</summary>
    public ObservableCollection<Choice> Choices { get; } = [];

    [ObservableProperty]
    public partial Choice? Selected { get; set; }

    /// <summary>The grid in words, or why there is none to switch on.</summary>
    [ObservableProperty]
    public partial string Hint { get; private set; } = string.Empty;

    /// <summary>The hint is a problem when the profile wants Surround on but there is no grid to switch to.</summary>
    [ObservableProperty]
    public partial bool HintIsError { get; private set; }

    /// <summary>The setting as it would be saved; <c>null</c> leaves Surround alone, and so does "on" without a grid.</summary>
    public SurroundSetting? Build() => Selected?.Key switch
    {
        // Without a setting of its own the profile already switches Surround off; writing it would only count as a change.
        Off when _offByDefault => null,
        Off => new SurroundSetting { Enabled = false },
        On when _grid is { } grid => new SurroundSetting { Enabled = true, Grid = grid },
        _ => null,
    };

    /// <summary>The texts in the current language; the answer stays.</summary>
    public void Relabel()
    {
        if (IsVisible)
        {
            FillChoices(Selected?.Key ?? Unchanged);
        }
    }

    partial void OnSelectedChanged(Choice? value)
    {
        if (_filling)
        {
            return;
        }

        UpdateHint();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void FillChoices(string selectedKey)
    {
        // Clearing the list makes a bound list box drop its selection; that is no answer from the user.
        _filling = true;
        try
        {
            Choices.Clear();
            if (_usedBy is null)
            {
                Choices.Add(new Choice(Unchanged, Loc.Instance["Editor_SurroundUnchanged"]));
            }

            Choices.Add(new Choice(Off, Loc.Instance["Editor_SurroundOff"]));
            Choices.Add(new Choice(On, Loc.Instance["Editor_SurroundOn"]));
            Selected = Choices.FirstOrDefault(c => c.Key == selectedKey) ?? Choices[0];
        }
        finally
        {
            _filling = false;
        }

        UpdateHint();
    }

    private void UpdateHint()
    {
        if (_offByDefault && Selected?.Key == Off)
        {
            Hint = Loc.Format("Editor_SurroundOffBecause", _usedBy!);
            HintIsError = false;
            return;
        }

        if (_grid is { } grid)
        {
            Hint = Loc.Format("Editor_SurroundGrid", grid.Displays.Count, grid.Width, grid.Height, grid.TotalWidth, grid.TotalHeight);
            HintIsError = false;
            return;
        }

        Hint = _state.Availability == SurroundAvailability.Available
            ? Loc.Instance["Editor_SurroundNoGrid"]
            : Loc.Instance["Editor_SurroundNoDriver"];
        HintIsError = Selected?.Key == On;
    }
}
