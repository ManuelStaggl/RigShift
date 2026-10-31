using CommunityToolkit.Mvvm.ComponentModel;

namespace RigShift.App.ViewModels;

/// <summary>One value of a game's settings file in the editor's checklist.</summary>
/// <param name="Value">The value as the file holds it now; <c>null</c> for a kept key the file no longer has.</param>
public sealed partial class SettingsKeyChoice(string key, string? value, bool isChecked) : ObservableObject
{
    public string Key { get; } = key;

    public string Value { get; } = value ?? "–";

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    /// <summary>Matches the search; the list hides the rest.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}
