using System.IO;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>The app row says in words what is wrong with its path; colour alone does not carry it.</summary>
public sealed class AppEditItemTests
{
    private static readonly string Existing = Path.Combine(Environment.SystemDirectory, "notepad.exe");

    [Fact]
    public void PathNote_FileExists_IsNull()
    {
        new AppEditItem(new AppAction { Path = Existing }).PathNote.ShouldBeNull();
    }

    [Fact]
    public void PathNote_FullPathWithoutFile_SaysNotFound()
    {
        var row = new AppEditItem(new AppAction { Path = @"C:\nowhere\missing-rigshift-test.exe" });

        row.PathNote.ShouldBe(Loc.Instance["App_NotFound"]);
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
    public void PathNote_FollowsPathAndKind()
    {
        var row = new AppEditItem(new AppAction { Path = "notepad.exe" });
        List<string?> changed = [];
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.SelectedKind = row.KindChoices[1];
        row.PathNote.ShouldBeNull();
        row.Path = Existing;

        changed.Count(name => name == nameof(AppEditItem.PathNote)).ShouldBe(2);
    }
}
