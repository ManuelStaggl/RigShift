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
/// Stroke drawings on a 24 × 24 grid: the brand package's profile symbols (from the WinUI app's ProfileGlyphs) and the
/// generic icons, drawn for the spike in the same stroke style (no icon font, no Segoe glyphs).
/// </summary>
public static class Glyphs
{
    public const string Overview = "M3 4 H10 V11 H3 Z M14 4 H21 V8 H14 Z M14 12 H21 V20 H14 Z M3 15 H10 V20 H3 Z";
    public const string Profiles = "M2 4 H22 V15 H2 Z M12 15 V19 M7 19 H17";
    public const string Games = "M7 6 H17 Q21 6 21.5 10 L22 15 Q22 18.5 19 18.5 Q17.5 18.5 16.5 17 L15.5 15.5 H8.5 L7.5 17 Q6.5 18.5 5 18.5 Q2 18.5 2 15 L2.5 10 Q3 6 7 6 Z M6 11 H10 M8 9 V13 M15 10.5 V10.6 M17.5 12.5 V12.6";
    public const string Fov = "M12 19 L3 6 M12 19 L21 6 M6.2 10.6 Q12 7 17.8 10.6";
    public const string Settings = "M4 6 H13 M17 6 H20 M15 4 V8 M4 12 H7 M11 12 H20 M9 10 V14 M4 18 H13 M17 18 H20 M15 16 V20";
    public const string Help = "M12 3 A9 9 0 1 1 11.99 3 Z M9.5 9.5 Q9.5 7 12 7 Q14.5 7 14.5 9.3 Q14.5 11 12 12 V13.5 M12 16.5 V16.6";
    public const string More = "M5 12 V12.1 M12 12 V12.1 M19 12 V12.1";
    public const string ChevronDown = "M6 9 L12 15 L18 9";
    public const string Plus = "M12 5 V19 M5 12 H19";
    public const string Trash = "M4 7 H20 M10 11 V17 M14 11 V17 M6 7 L7 20 H17 L18 7 M9 7 V4 H15 V7";
    public const string Up = "M12 19 V5 M6 11 L12 5 L18 11";
    public const string Down = "M12 5 V19 M6 13 L12 19 L18 13";
    public const string Play = "M8 5 L19 12 L8 19 Z";
    public const string Audio = "M11 5 L6 9 H3 V15 H6 L11 19 Z M15.5 8.5 Q17 10 17 12 Q17 14 15.5 15.5 M18.5 5.5 Q21 8.5 21 12 Q21 15.5 18.5 18.5";
    public const string Apps = "M3 3 H10 V10 H3 Z M14 3 H21 V10 H14 Z M3 14 H10 V21 H3 Z M14 14 H21 V21 H14 Z";
    public const string Triggers = "M9 2 V7 M15 2 V7 M6 7 H18 V11 Q18 17 12 17 Q6 17 6 11 Z M12 17 V22";
    public const string Behavior = "M7 7 H17 Q22 7 22 12 Q22 17 17 17 H7 Q2 17 2 12 Q2 7 7 7 Z M16 10 Q18 10 18 12 Q18 14 16 14 Q14 14 14 12 Q14 10 16 10 Z";
    public const string Window = "M3 5 H21 V19 H3 Z M3 9 H21";
    public const string Copy = "M8 8 H20 V20 H8 Z M16 8 V4 H4 V16 H8";
    public const string Close = "M6 6 L18 18 M18 6 L6 18";
    public const string Display = "M3 4 H21 V15 H3 Z M12 15 V19 M8 19 H16";

    /// <summary>The brand stroke drawing of a profile symbol; a profile without one gets the desk monitor.</summary>
    public static string ProfileData(string? key) => ProfileIcons.Normalize(key) switch
    {
        ProfileIcons.Rig => "M4 20 H21 M7 20 L10 14 H16 L19 20 M5 4 V11 Q5 14 8 14 H13 M5 5 L3 3 M16 6 L19 9 M17.5 7.5 L13 12 M14 5 Q18 3 21 8",
        ProfileIcons.Vr => "M5 7 H19 Q22 7 22 10 V15 Q22 17 19 17 H16 L13 14 H11 L8 17 H5 Q2 17 2 15 V10 Q2 7 5 7 Z M2 11 H1 M22 11 H23",
        ProfileIcons.Tv => "M4 2 H20 V11 H4 Z M10 11 V14 M7 17 V15 H17 V17 M4 16 V21 H20 V16 M4 19 H20 M7 21 V23 M17 21 V23",
        ProfileIcons.Stream => "M12 3 Q9 3 9 6 V11 Q9 14 12 14 Q15 14 15 11 V6 Q15 3 12 3 Z M6 10 V11 Q6 17 12 17 Q18 17 18 11 V10 M12 17 V21 M8 21 H16 M3 4 V8 M21 4 V8",
        _ => "M3 4 H21 V15 H3 Z M12 15 V19 M8 19 H16 M2 21 V18 M22 21 V18 M2 18 H22",
    };

    public static Geometry Profile(string? key) => Geometry.Parse(ProfileData(key));
}

/// <summary>The generic icons as shared geometries for <c>{x:Static}</c> in XAML; Avalonia lets paths share one geometry.</summary>
public static class Icons
{
    public static readonly Geometry Play = Geometry.Parse(Glyphs.Play);
    public static readonly Geometry Plus = Geometry.Parse(Glyphs.Plus);
    public static readonly Geometry More = Geometry.Parse(Glyphs.More);
    public static readonly Geometry Trash = Geometry.Parse(Glyphs.Trash);
    public static readonly Geometry Up = Geometry.Parse(Glyphs.Up);
    public static readonly Geometry Down = Geometry.Parse(Glyphs.Down);
    public static readonly Geometry Audio = Geometry.Parse(Glyphs.Audio);
    public static readonly Geometry Apps = Geometry.Parse(Glyphs.Apps);
    public static readonly Geometry Triggers = Geometry.Parse(Glyphs.Triggers);
    public static readonly Geometry Behavior = Geometry.Parse(Glyphs.Behavior);
    public static readonly Geometry Window = Geometry.Parse(Glyphs.Window);
    public static readonly Geometry Copy = Geometry.Parse(Glyphs.Copy);
    public static readonly Geometry Close = Geometry.Parse(Glyphs.Close);
    public static readonly Geometry ChevronDown = Geometry.Parse(Glyphs.ChevronDown);
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
    /// <summary>An icon key to the profile's stroke geometry.</summary>
    public static readonly IValueConverter ProfileGeometry = new FuncValueConverter<string?, Geometry>(Glyphs.Profile);

    /// <summary>A path-data string constant to a geometry.</summary>
    public static readonly IValueConverter PathData = new FuncValueConverter<string?, Geometry?>(s => s is null ? null : Geometry.Parse(s));

    /// <summary>PNG bytes from <see cref="Services.SpikeIcons"/> to a bitmap.</summary>
    public static readonly IValueConverter IconImage = new FuncValueConverter<object?, Bitmap?>(icon =>
        icon is byte[] png ? new Bitmap(new MemoryStream(png)) : null);

    /// <summary>
    /// Two-way selection without writing a transient <c>null</c> back: a new list clears the selection for a moment, and
    /// that null would change the profile and mark it unsaved (the WinUI page's BindBack handlers).
    /// </summary>
    public static readonly IValueConverter KeepOnNull = new KeepOnNullConverter();

    /// <summary><c>double?</c> seconds to the <c>decimal?</c> of NumericUpDown and back.</summary>
    public static readonly IValueConverter Seconds = new SecondsConverter();

    private sealed class KeepOnNullConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value ?? BindingOperations.DoNothing;
    }

    private sealed class SecondsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double d ? (decimal)d : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is decimal m ? (double)m : null;
    }
}
