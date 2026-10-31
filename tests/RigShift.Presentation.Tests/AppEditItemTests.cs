using System.IO;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>The app row says in words what is wrong with its path; colour alone does not carry it.</summary>
public sealed class AppEditItemTests
{
    private static readonly string Existing = Path.Combine(Environment.SystemDirectory, "notepad.exe");

    [Fact]
    public void Wait_WindowChosenWithoutSeconds_ShowsTheMinuteItWaitsAtMost()
    {
        var row = new AppEditItem(new AppAction { Path = Existing });
        row.ShowWaitSeconds.ShouldBeFalse();

        row.SelectedWait = row.WaitChoices[(int)AppWait.Window];

        row.ShowWaitSeconds.ShouldBeTrue();
        row.WaitSeconds.ShouldBe(AppAction.DefaultWindowWaitSeconds);
        row.ToAction().ShouldSatisfyAllConditions(
            action => action.Wait.ShouldBe(AppWait.Window),
            action => action.WaitSeconds.ShouldBe(AppAction.DefaultWindowWaitSeconds));
    }

    [Fact]
    public void Wait_WindowChosenWithSeconds_KeepsThem()
    {
        var row = new AppEditItem(new AppAction { Path = Existing, Wait = AppWait.Fixed, WaitSeconds = 20 });

        row.SelectedWait = row.WaitChoices[(int)AppWait.Window];

        row.ToAction().WaitSeconds.ShouldBe(20);
    }

    [Fact]
    public void Wait_NoneChosen_SavesNoSeconds()
    {
        var row = new AppEditItem(new AppAction { Path = Existing, Wait = AppWait.Fixed, WaitSeconds = 20 });

        row.SelectedWait = row.WaitChoices[(int)AppWait.None];

        row.ShowWaitSeconds.ShouldBeFalse();
        row.ToAction().ShouldSatisfyAllConditions(
            action => action.Wait.ShouldBe(AppWait.None),
            action => action.WaitSeconds.ShouldBe(0));
    }

    /// <summary>A 4.x entry opens with the one choice it amounts to, and saves unchanged.</summary>
    [Theory]
    [InlineData(AppActionKind.Start, 0, false, AppWait.None)]
    [InlineData(AppActionKind.Start, 5, false, AppWait.Fixed)]
    [InlineData(AppActionKind.Start, 0, true, AppWait.Window)]
    [InlineData(AppActionKind.Start, 20, true, AppWait.Window)]
    [InlineData(AppActionKind.Stop, 5, true, AppWait.Fixed)]
    [InlineData(AppActionKind.Stop, 0, true, AppWait.None)]
    public void Load_OldEntry_ShowsItsWait_AndSavesTheSame(AppActionKind kind, int seconds, bool waitForWindow, AppWait wait)
    {
        var old = new AppAction { Kind = kind, Path = Existing, WaitSeconds = seconds, LegacyWaitForWindow = waitForWindow };
        var row = new AppEditItem(old);

        row.SelectedWait!.Key.ShouldBe(wait.ToString());
        row.ToAction().ShouldSatisfyAllConditions(
            action => action.Wait.ShouldBe(old.Wait),
            action => action.WaitSeconds.ShouldBe(old.WaitSeconds));
    }

    [Fact]
    public void Wait_OnAStop_HasNoSecondsField()
    {
        var row = new AppEditItem(new AppAction { Kind = AppActionKind.Stop, Path = "notepad.exe", Wait = AppWait.Fixed, WaitSeconds = 5 });

        row.ShowWaitSeconds.ShouldBeFalse();
    }

    [Fact]
    public async Task PathNote_FileExists_IsNull_AndTheIconIsThere()
    {
        // Any image will do: the app's own extraction is UI code.
        AppIcons.Extract = file => file;
        try
        {
            var row = new AppEditItem(new AppAction { Path = Existing });

            await row.PathChecked;

            row.PathNote.ShouldBeNull();
            row.Icon.ShouldNotBeNull();
        }
        finally
        {
            AppIcons.Extract = _ => null;
        }
    }

    [Fact]
    public async Task PathNote_FullPathWithoutFile_SaysNotFound()
    {
        var row = new AppEditItem(new AppAction { Path = @"C:\nowhere\missing-rigshift-test.exe" });

        await row.PathChecked;

        row.PathNote.ShouldBe(Loc.Instance["App_NotFound"]);
    }

    /// <summary>A path on a sleeping NAS froze the editor for the network's timeout, at every key.</summary>
    [Fact]
    public void PathNote_NetworkPath_IsNotLookedFor()
    {
        var row = new AppEditItem(new AppAction { Path = @"\\nas-asleep\tools\SimHub.exe" });

        row.PathChecked.IsCompleted.ShouldBeTrue();
        row.PathNote.ShouldBeNull();
        row.Icon.ShouldBeNull();
    }

    [Fact]
    public void PathNote_StartWithoutFullPath_SaysItWillNotStart()
    {
        var row = new AppEditItem(new AppAction { Path = "notepad.exe" });

        row.PathNote.ShouldBe(Loc.Instance["Restore_WarnNotFullPath"]);
    }

    [Fact]
    public void PathNote_StopByName_IsNull()
    {
        var row = new AppEditItem(new AppAction { Kind = AppActionKind.Stop, Path = "notepad.exe" });

        row.PathNote.ShouldBeNull();
    }

    [Fact]
    public void PathNote_EmptyPath_IsNull()
    {
        new AppEditItem(new AppAction { Path = string.Empty }).PathNote.ShouldBeNull();
    }

    [Fact]
    public async Task PathNote_FollowsPathAndKind()
    {
        var row = new AppEditItem(new AppAction { Path = "notepad.exe" }, time: new AutoAdvanceTimeProvider());
        row.PathNote.ShouldBe(Loc.Instance["Restore_WarnNotFullPath"]);

        row.SelectedKind = row.KindChoices[1];
        row.PathNote.ShouldBeNull();

        row.Path = @"C:\nowhere\missing-rigshift-test.exe";
        await row.PathChecked;
        row.PathNote.ShouldBe(Loc.Instance["App_NotFound"]);

        row.Path = Existing;
        await row.PathChecked;
        row.PathNote.ShouldBeNull();
    }
}
