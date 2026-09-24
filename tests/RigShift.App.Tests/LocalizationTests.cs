using System.Collections;
using System.Globalization;
using System.Resources;
using RigShift.App.Localization;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
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

    /// <summary>
    /// Some texts are looked up by an enum member's name ("Problem_" + name), so no search for the key finds its users:
    /// the 3.0 cleanup dropped four problem texts as unused, and a warning added in 2.1.2 never got one. Both showed the
    /// bare key.
    /// </summary>
    [Fact]
    public void EveryTextLookedUpByName_Exists()
    {
        IEnumerable<string> wanted =
        [
            .. Enum.GetNames<ProfileProblem>().Select(name => "Problem_" + name),
            .. Enum.GetNames<GameProblem>().Select(name => "Problem_" + name),
            .. Enum.GetNames<PlanWarningKind>().Select(name => "Warning_" + name),
            .. Enum.GetNames<SwitchOutcome>().Select(name => "Outcome_" + name),
            .. Enum.GetNames<GameSessionOutcome>().Select(name => "GameOutcome_" + name),
            .. Enum.GetNames<AllDisplaysOnOutcome>().Select(name => "AllOn_" + name),
            .. ProfileIcons.All.Select(key => "Icon_" + char.ToUpperInvariant(key[0]) + key[1..]),
        ];

        string[] missing = [.. wanted.Distinct().Except(Keys(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)];

        missing.ShouldBeEmpty($"missing in Strings.resx: {string.Join(", ", missing)}");
    }

    private static IEnumerable<string> Keys(CultureInfo culture)
    {
        var resources = new ResourceManager("RigShift.App.Resources.Strings", typeof(Loc).Assembly);
        ResourceSet set = resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for {culture.Name}");

        return set.Cast<DictionaryEntry>().Select(e => (string)e.Key);
    }
}
