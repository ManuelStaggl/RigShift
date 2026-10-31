using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RigShift.App.Localization;
using RigShift.Core.Profiles;

namespace RigShift.Avalonia.Views;

/// <summary>
/// A profile's symbol as a Fluent icon (Views/Icons.cs); the stored keys stay the same. Only 20 and 24: Fluent draws no
/// 16 px headset.
/// </summary>
public static class ProfileSymbols
{
    public static Geometry For(string? key, int size) => (ProfileIcons.Normalize(key), size >= 24) switch
    {
        (ProfileIcons.Rig, false) => Icons.VehicleCarProfile20,
        (ProfileIcons.Rig, true) => Icons.VehicleCarProfile24,
        (ProfileIcons.Vr, false) => Icons.HeadsetVr20,
        (ProfileIcons.Vr, true) => Icons.HeadsetVr24,
        (ProfileIcons.Tv, false) => Icons.Tv20,
        (ProfileIcons.Tv, true) => Icons.Tv24,
        (ProfileIcons.Stream, false) => Icons.Mic20,
        (ProfileIcons.Stream, true) => Icons.Mic24,
        (_, false) => Icons.Desktop20,
        (_, true) => Icons.Desktop24,
    };
}

/// <summary><c>{v:Tr Nav_Overview}</c>: a text of the shared strings in the app's language, without the WPF access key.</summary>
public sealed class Tr(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.WithoutAccessKey(Loc.Instance[Key]);
}

/// <summary>Small converters the views share.</summary>
public static class Converters
{
    /// <summary>An icon key to the profile's Fluent symbol; the parameter is the size (20 or 24).</summary>
    public static readonly IValueConverter ProfileSymbol = new FuncValueConverter<string?, string?, Geometry>((key, size) =>
        ProfileSymbols.For(key, int.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 20));

    /// <summary>PNG bytes from <see cref="Services.ProgramIcons"/> to a bitmap.</summary>
    public static readonly IValueConverter IconImage = new FuncValueConverter<object?, Bitmap?>(icon =>
        icon is byte[] png ? new Bitmap(new MemoryStream(png)) : null);

    /// <summary>
    /// Two-way selection without writing a transient <c>null</c> back: a new list clears the selection for a moment, and
    /// that null would change the profile and mark it unsaved (the WinUI page's BindBack handlers).
    /// </summary>
    public static readonly IValueConverter KeepOnNull = new KeepOnNullConverter();

    /// <summary>The editor's problem count as "2 problems" on the save bar; empty without any.</summary>
    public static readonly IValueConverter ProblemCount = new FuncValueConverter<int, string>(count =>
        count == 0 ? string.Empty : Loc.Format(count == 1 ? "SaveBar_Problem" : "SaveBar_Problems", count));

    /// <summary>An enum value (or anything) equal to the parameter by name, for <c>Classes.x</c> bindings.</summary>
    public static readonly IValueConverter Is = new FuncValueConverter<object?, string?, bool>((value, name) => value?.ToString() == name);

    /// <summary>A shared text with its WPF access key ("C_hoose") as plain text.</summary>
    public static readonly IValueConverter NoAccessKey = new FuncValueConverter<string?, string?>(s => s is null ? null : Loc.WithoutAccessKey(s));

    /// <summary><c>double?</c> seconds to the <c>decimal?</c> of NumericUpDown and back.</summary>
    public static readonly IValueConverter Seconds = new SecondsConverter();

    /// <summary><c>double</c> to the <c>decimal?</c> of NumericUpDown and back; an emptied field keeps the last value.</summary>
    public static readonly IValueConverter Number = new NumberConverter();

    private sealed class KeepOnNullConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value ?? BindingOperations.DoNothing;
    }

    private sealed class NumberConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double d ? (decimal)d : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is decimal m ? (double)m : BindingOperations.DoNothing;
    }

    private sealed class SecondsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double d ? (decimal)d : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is decimal m ? (double)m : null;
    }
}
