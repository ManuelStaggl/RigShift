using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
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
    /// <summary>A list or control belongs to the thread that made it; a language change must reach it there.</summary>
    [Fact]
    public void LanguageChange_ReachesEachListenerOnItsOwnThread()
    {
        string before = Loc.Instance.UICulture.Name;
        using var ui = new DispatcherThread();
        int calledOn = 0;
        PropertyChangedEventHandler handler = (_, _) => calledOn = Environment.CurrentManagedThreadId;
        ui.Invoke(() => Loc.Instance.PropertyChanged += handler);
        try
        {
            Loc.Instance.SetLanguage(before == "de" ? "en" : "de");
            ui.Drain();

            calledOn.ShouldBe(ui.ThreadId);
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
            Loc.Instance.SetLanguage(before);
        }
    }

    /// <summary>The base context has no thread to go back to; the pool would run two handlers of one object at once.</summary>
    [Fact]
    public void LanguageChange_ListenerOnAThreadWithoutItsOwnContext_RunsOnTheChangingThread()
    {
        string before = Loc.Instance.UICulture.Name;
        int calledOn = 0;
        PropertyChangedEventHandler handler = (_, _) => calledOn = Environment.CurrentManagedThreadId;
        var subscriber = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            Loc.Instance.PropertyChanged += handler;
        });
        subscriber.Start();
        subscriber.Join();
        try
        {
            Loc.Instance.SetLanguage(before == "de" ? "en" : "de");

            calledOn.ShouldBe(Environment.CurrentManagedThreadId);
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
            Loc.Instance.SetLanguage(before);
        }
    }

    [Fact]
    public void SameLanguageAgain_ChangesNothing()
    {
        int changes = 0;
        PropertyChangedEventHandler handler = (_, _) => changes++;
        Loc.Instance.PropertyChanged += handler;
        try
        {
            Loc.Instance.SetLanguage(Loc.Instance.UICulture.Name);

            changes.ShouldBe(0);
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
        }
    }

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

    /// <summary>
    /// One word per thing (v4 findings U-15…U-18): a layout is not also an arrangement or a setup – in sim racing a
    /// setup is the car's –, a hotkey is not also a shortcut, and English is American like Windows. Pages that were
    /// renamed must not live on in texts that point at them.
    /// </summary>
    [Theory]
    [MemberData(nameof(WordsNotToUse))]
    public void Texts_UseTheGlossary(string culture, string pattern, string instead)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        string[] found = [.. Entries(culture == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture))
            .Where(e => !GlossaryExceptions.Contains(e.Key) && regex.IsMatch(e.Value))
            .Select(e => $"{e.Key}: {e.Value}")];

        found.ShouldBeEmpty($"use {instead}");
    }

    public static TheoryData<string, string, string> WordsNotToUse() => new()
    {
        { "en", @"arrangement", "“layout”" },
        { "en", @"\bsetups\b|\b(first|second|desk|rig|your) setup\b", "“layout” – a setup is the car's" },
        { "en", @"keyboard shortcut|(?<!desktop )\bshortcuts?\b", "“hotkey”, or “desktop shortcut” for the file" },
        { "en", @"previous profile", "“Switch back” / “last profile”" },
        { "en", @"^(Unchanged|Leave|Apply again|Stay in the profile)$", "“Don't change” / “Re-apply”" },
        { "en", @"About & help|profile editor|automation rule", "the current page names: Help, Profiles, USB rules" },
        { "en", @"recognis|\bcentre|neighbour|colour|behaviour|\bgrey|favourite|metres?\b|customis|analys|cancelled|\btick", "American spelling" },
        { "de", @"\bAufbau(ten)?\b", "„Anordnung“" },
        { "de", @"Hotkey", "„Tastenkürzel“" },
        { "de", @"Vorheriges Profil|Automatik-Regel|Über & Hilfe|Profileditor", "die aktuellen Namen: Zurückschalten, USB-Regel, Hilfe, Profile" },
    };

    /// <summary>Texts that quote a sim's own menu, which has its own spelling.</summary>
    private static readonly HashSet<string> GlossaryExceptions = ["Fov_Where_F1"];

    private static IEnumerable<string> Keys(CultureInfo culture) => Entries(culture).Select(e => e.Key);

    private static IEnumerable<KeyValuePair<string, string>> Entries(CultureInfo culture)
    {
        var resources = new ResourceManager("RigShift.App.Resources.Strings", typeof(Loc).Assembly);
        ResourceSet set = resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for {culture.Name}");

        return set.Cast<DictionaryEntry>().Select(e => new KeyValuePair<string, string>((string)e.Key, e.Value as string ?? string.Empty));
    }
}
