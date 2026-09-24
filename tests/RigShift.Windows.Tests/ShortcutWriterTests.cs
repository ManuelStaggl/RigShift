using RigShift.Windows.Shell;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>A renamed profile takes RigShift's own desktop shortcut along, and nothing else (v4 finding U-12).</summary>
public sealed class ShortcutWriterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rigshift-shortcuts-").FullName;
    private readonly string _executable;

    public ShortcutWriterTests()
    {
        _executable = Path.Combine(_folder, "RigShift.exe");
        File.WriteAllBytes(_executable, []);
    }

    [Fact]
    public void Retarget_OwnShortcut_GetsTheNewNameAndFile()
    {
        string oldFile = Path.Combine(_folder, "RigShift – Rig.lnk");
        string newFile = Path.Combine(_folder, "RigShift – Triples.lnk");
        ShortcutWriter.Create(oldFile, _executable, "apply \"Rig\"", "Switch to Rig");

        ShortcutWriter.Retarget(oldFile, newFile, _executable, "apply \"Rig\"", "apply \"Triples\"", "Switch to Triples").ShouldBeTrue();

        File.Exists(oldFile).ShouldBeFalse();
        File.Exists(newFile).ShouldBeTrue();
        ShortcutWriter.Retarget(newFile, newFile, _executable, "apply \"Triples\"", "apply \"Triples\"", "same").ShouldBeTrue(
            "the new shortcut carries the new arguments");
    }

    [Fact]
    public void Retarget_ShortcutOfAnotherProgramWithTheSameName_StaysUntouched()
    {
        string game = Path.Combine(_folder, "iRacing.exe");
        File.WriteAllBytes(game, []);
        string installerShortcut = Path.Combine(_folder, "iRacing.lnk");
        ShortcutWriter.Create(installerShortcut, game, string.Empty, "iRacing");

        ShortcutWriter.Retarget(installerShortcut, Path.Combine(_folder, "iRacing Oval.lnk"), _executable, "play \"iRacing\"", "play \"iRacing Oval\"", "x")
            .ShouldBeFalse();

        File.Exists(installerShortcut).ShouldBeTrue();
        File.Exists(Path.Combine(_folder, "iRacing Oval.lnk")).ShouldBeFalse();
    }

    [Fact]
    public void Retarget_NoShortcut_DoesNothing() =>
        ShortcutWriter.Retarget(Path.Combine(_folder, "none.lnk"), Path.Combine(_folder, "new.lnk"), _executable, "apply \"A\"", "apply \"B\"", "x")
            .ShouldBeFalse();

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
