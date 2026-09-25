using System.IO;
using RigShift.App.Localization;
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
    public void WaitForWindow_TickedWithoutSeconds_ShowsTheMinuteItWaitsAtMost()
    {
        var row = new AppEditItem(new AppAction { Path = Existing });

        row.WaitForWindow = true;

        row.WaitSeconds.ShouldBe(AppEditItem.DefaultWindowWaitSeconds);
        row.ToAction().ShouldSatisfyAllConditions(
            action => action.WaitForWindow.ShouldBeTrue(),
            action => action.WaitSeconds.ShouldBe(AppEditItem.DefaultWindowWaitSeconds));
    }

    [Fact]
    public void WaitForWindow_TickedWithSeconds_KeepsThem()
    {
        var row = new AppEditItem(new AppAction { Path = Existing, WaitSeconds = 20 });

        row.WaitForWindow = true;

        row.ToAction().WaitSeconds.ShouldBe(20);
    }

    [Fact]
    public void WaitForWindow_OnAStop_IsNotSaved()
    {
        var row = new AppEditItem(new AppAction { Kind = AppActionKind.Stop, Path = "notepad.exe", WaitForWindow = true });

        row.ToAction().WaitForWindow.ShouldBeFalse();
    }

    [Fact]
    public async Task PathNote_FileExists_IsNull_AndTheIconIsThere()
    {
        var row = new AppEditItem(new AppAction { Path = Existing });

        await row.PathChecked;

        row.PathNote.ShouldBeNull();
        row.Icon.ShouldNotBeNull();
    }

    [Fact]
    public async Task PathNote_FullPathWithoutFile_SaysNotFound()
    {
        var row = new AppEditItem(new AppAction { Path = @"C:\nowhere\missing-rigshift-test.exe" });

        await row.PathChecked;

        row.PathNote.ShouldBe(Loc.Instance["App_NotFound"]);
    }

    /// <summary>A-12: a path on a sleeping NAS froze the editor for the network's timeout, at every key.</summary>
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
