using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.Views;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>The lines of the restore dialog: what the backup would do, in words, with the warning spelled out.</summary>
public sealed class RestoreReviewTests
{
    [Fact]
    public void Describe_ProgramOnAShare_SaysSoInWords()
    {
        DialogDetail detail = ProfileDialogs.Describe(
            new BackupItem(BackupItemKind.StartsProgram, "Sim Rig", @"\\nas\tools\x.exe -a", IsNetworkPath: true));

        detail.Heading.ShouldBe($"{Loc.Instance["Restore_Starts"]} · Sim Rig");
        detail.Text.ShouldBe(@"\\nas\tools\x.exe -a");
        detail.Warning.ShouldBe(Loc.Instance["Restore_WarnNetwork"]);
    }

    [Fact]
    public void Describe_PlainProgram_HasNoWarning()
    {
        ProfileDialogs.Describe(new BackupItem(BackupItemKind.StopsProgram, "Desk", "discord")).Warning.ShouldBeNull();
    }

    [Fact]
    public void Describe_RuleWithoutConfirmation_IsMarked()
    {
        DialogDetail detail = ProfileDialogs.Describe(new BackupItem(BackupItemKind.Rule, "Sim Rig", "Wheel", SkipsConfirmation: true));

        detail.Heading.ShouldStartWith(Loc.Instance["Restore_Rule"]);
        detail.Warning.ShouldBe(Loc.Instance["Restore_WarnNoConfirm"]);
    }

    /// <summary>The "previous profile" hotkey belongs to no profile; the line names the setting instead.</summary>
    [Fact]
    public void Describe_ToggleHotkey_NamesTheSetting()
    {
        var keys = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 };

        DialogDetail detail = ProfileDialogs.Describe(new BackupItem(BackupItemKind.Hotkey, string.Empty, string.Empty, Keys: keys));

        detail.Heading.ShouldContain(Loc.Instance["Settings_ToggleHotkey"]);
        detail.Text.ShouldBe(HotkeyFormat.Format(keys));
    }
}
