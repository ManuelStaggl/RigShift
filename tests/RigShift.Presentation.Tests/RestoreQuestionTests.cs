using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>The lines of the restore question: what the backup would do, in words, with the warning spelled out.</summary>
public sealed class RestoreQuestionTests
{
    [Fact]
    public void Describe_ProgramOnAShare_SaysSoInWords()
    {
        RestoreLine line = RestoreQuestion.Describe(
            new BackupItem(BackupItemKind.StartsProgram, "Sim Rig", @"\\nas\tools\x.exe -a", IsNetworkPath: true));

        line.Heading.ShouldBe($"{Loc.Instance["Restore_Starts"]} · Sim Rig");
        line.Text.ShouldBe(@"\\nas\tools\x.exe -a");
        line.Warning.ShouldBe(Loc.Instance["Restore_WarnNetwork"]);
    }

    [Fact]
    public void Describe_PlainProgram_HasNoWarning()
    {
        RestoreQuestion.Describe(new BackupItem(BackupItemKind.StopsProgram, "Desk", "discord")).Warning.ShouldBeNull();
    }

    [Fact]
    public void Describe_RuleWithoutConfirmation_IsMarked()
    {
        RestoreLine line = RestoreQuestion.Describe(new BackupItem(BackupItemKind.Rule, "Sim Rig", "Wheel", SkipsConfirmation: true));

        line.Heading.ShouldStartWith(Loc.Instance["Restore_Rule"]);
        line.Warning.ShouldBe(Loc.Instance["Restore_WarnNoConfirm"]);
    }

    /// <summary>The "previous profile" hotkey belongs to no profile; the line names the setting instead.</summary>
    [Fact]
    public void Describe_ToggleHotkey_NamesTheSetting()
    {
        var keys = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 };

        RestoreLine line = RestoreQuestion.Describe(new BackupItem(BackupItemKind.Hotkey, string.Empty, string.Empty, Keys: keys));

        line.Heading.ShouldContain(Loc.Instance["Settings_ToggleHotkey"]);
        line.Text.ShouldBe(HotkeyFormat.Format(keys));
    }

    /// <summary>A backup with programs on a share says there is something to check, and lists that line first.</summary>
    [Fact]
    public void Lines_AttentionFirst_AndTheMessageSaysSo()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Rig",
            Displays = [],
            Apps =
            [
                new AppAction { Path = @"C:\Tools\SimHub.exe" },
                new AppAction { Path = @"\\nas\tools\CrewChief.exe" },
            ],
        };
        var content = new BackupContent([profile], null);

        RestoreQuestion.Lines(content)[0].Warning.ShouldBe(Loc.Instance["Restore_WarnNetwork"]);
        RestoreQuestion.Message(content).ShouldEndWith(Loc.Instance["About_RestoreReviewAttention"]);
    }
}
