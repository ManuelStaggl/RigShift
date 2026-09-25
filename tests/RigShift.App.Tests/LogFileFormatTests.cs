using System.IO;
using RigShift.App.Services;
using Serilog;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class LogFileFormatTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rigshift-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        LogFileHeader.AppState = null;
        LogFileHeader.Enabled = false;
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void TrayApp_HeadsItsFirstLine_WithVersionWindowsAndAppState_CommandLineDoesNot()
    {
        LogFileHeader.AppState = () => "4 of 4 displays active";
        LogFileHeader.Enabled = true;
        var paths = new AppPaths(_directory);

        // Two processes share the day's file: only the tray app writes the head, once per day.
        using (Logger first = (Logger)AppLogging.Create(paths))
        using (Logger second = (Logger)AppLogging.Create(paths))
        {
            first.Information("from the tray app");
            first.Information("again from the tray app");
            LogFileHeader.Enabled = false;
            second.Information("from a command line");
        }

        string[] lines = File.ReadAllLines(Directory.GetFiles(Path.Combine(_directory, "logs")).Single());
        lines[0].ShouldStartWith("# RigShift ");
        lines[0].ShouldContain("Windows ");
        lines.ShouldContain("# 4 of 4 displays active");
        lines.Count(l => l.StartsWith("# RigShift ", StringComparison.Ordinal)).ShouldBe(1);
        lines.ShouldContain(l => l.EndsWith("App: from the tray app", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_ShowTheProfileFolderAsVariable_NotTheUserName()
    {
        var paths = new AppPaths(_directory);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        using (Logger log = (Logger)AppLogging.Create(paths))
        {
            log.Information("Saved {File}", Path.Combine(appData, "RigShift", "profiles", "Rig.json"));
            log.Warning(new IOException($"Access to the path '{Path.Combine(profile, "Desktop", "x.lnk")}' is denied."), "Shortcut failed");
            log.Information("Preview written to {File}", Path.Combine(profile, "Pictures").Replace('\\', '/'));
        }

        string text = File.ReadAllText(Directory.GetFiles(Path.Combine(_directory, "logs")).Single());
        text.ShouldContain(@"%AppData%\RigShift\profiles\Rig.json");
        text.ShouldContain(@"%UserProfile%\Desktop\x.lnk");
        text.ShouldContain("%UserProfile%/Pictures");
        text.ShouldNotContain(profile, Case.Insensitive);
    }
}
