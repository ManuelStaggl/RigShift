using System.Collections;
using System.Globalization;
using System.Resources;
using RigShift.App.Localization;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>
/// Both languages have to carry the same keys. A key that only exists in English shows up as the bare key name in
/// the German UI – and nobody notices until a user does.
/// </summary>
public sealed class LocalizationTests
{
    [Fact]
    public void German_HasEveryKeyOfEnglish()
    {
        string[] missing = [.. Keys(CultureInfo.InvariantCulture).Except(Keys(CultureInfo.GetCultureInfo("de"))).Order(StringComparer.Ordinal)];

        missing.ShouldBeEmpty($"missing in Strings.de.resx: {string.Join(", ", missing)}");
    }

    [Fact]
    public void English_HasEveryKeyOfGerman()
    {
        string[] extra = [.. Keys(CultureInfo.GetCultureInfo("de")).Except(Keys(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)];

        extra.ShouldBeEmpty($"only in Strings.de.resx: {string.Join(", ", extra)}");
    }

    private static IEnumerable<string> Keys(CultureInfo culture)
    {
        var resources = new ResourceManager("RigShift.App.Resources.Strings", typeof(Loc).Assembly);
        ResourceSet set = resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for {culture.Name}");

        return set.Cast<DictionaryEntry>().Select(e => (string)e.Key);
    }
}
