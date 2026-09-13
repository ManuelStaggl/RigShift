using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace RigShift.App.Localization;

/// <summary>
/// String lookup for XAML (<c>{loc:Tr Key}</c>) and code. Switching the language raises a change for every
/// indexer binding, so open windows update without a restart.
/// </summary>
/// <remarks>
/// The chosen culture is kept here instead of relying on <see cref="CultureInfo.CurrentUICulture"/>: a change made
/// inside an async method is undone for the caller when the await returns (culture flows with the execution context).
/// </remarks>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    private readonly ResourceManager _resources = new("RigShift.App.Resources.Strings", typeof(Loc).Assembly);

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static Loc Instance { get; } = new();

    public CultureInfo Culture { get; private set; } = SystemCulture;

    public string this[string key] => _resources.GetString(key, Culture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(Instance.Culture, Instance[key], args);

    /// <param name="language"><c>null</c> follows Windows, otherwise a culture name such as <c>de</c>.</param>
    public void SetLanguage(string? language)
    {
        Culture = string.IsNullOrWhiteSpace(language) ? SystemCulture : CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
