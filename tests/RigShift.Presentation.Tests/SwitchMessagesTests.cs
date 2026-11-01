using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Velopack.Logging;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>The tray's notifications after a switch, after the emergency hotkey, and Velopack's lines in the log.</summary>
public sealed class SwitchMessagesTests
{
    [Fact]
    public void ForNotification_Applied_SaysSo()
    {
        (string title, string text, NoticeLevel icon) = SwitchMessages.ForNotification(Record(SwitchOutcome.Applied));

        title.ShouldBe(Loc.Format("Result_AppliedTitle", "Rig"));
        text.ShouldBe(Loc.Instance["Result_AppliedText"]);
        icon.ShouldBe(NoticeLevel.Info);
    }

    [Fact]
    public void ForNotification_AppliedWithOptionalDisplaysMissing_NamesThem()
    {
        (_, string text, _) = SwitchMessages.ForNotification(Record(SwitchOutcome.Applied) with { MissingDisplays = ["Tablet"] });

        text.ShouldBe(Loc.Format("Result_AppliedFollowUpText", "Tablet"));
    }

    [Theory]
    [InlineData(SwitchOutcome.AppliedPartially, NoticeLevel.Info)]
    [InlineData(SwitchOutcome.RolledBack, NoticeLevel.Warning)]
    [InlineData(SwitchOutcome.Blocked, NoticeLevel.Warning)]
    [InlineData(SwitchOutcome.Failed, NoticeLevel.Error)]
    public void ForNotification_EveryOutcome_HasATextAndItsLevel(SwitchOutcome outcome, NoticeLevel expected)
    {
        (string title, string text, NoticeLevel icon) = SwitchMessages.ForNotification(Record(outcome) with { MissingDisplays = ["Tablet"] });

        title.ShouldContain("Rig");
        text.ShouldNotBeNullOrWhiteSpace();
        icon.ShouldBe(expected);
    }

    [Fact]
    public void ForNotification_RolledBackWithoutSurround_SaysTheGridIsGone()
    {
        (_, string text, _) = SwitchMessages.ForNotification(Record(SwitchOutcome.RolledBack) with { Note = SwitchNote.SurroundNotRestored });

        text.ShouldBe(Loc.Instance["Result_RolledBackNoSurroundText"]);
    }

    [Fact]
    public void ForNotification_Ambiguous_SaysWhichDisplaysCouldNotBeTold()
    {
        (_, string text, _) = SwitchMessages.ForNotification(Record(SwitchOutcome.Blocked) with { MissingDisplays = ["CM27X3"], Ambiguous = true });

        text.ShouldBe(Loc.Format("Result_AmbiguousText", "CM27X3"));
    }

    [Theory]
    [InlineData(SwitchNote.RestoredPrevious, "Note_RestoredPrevious")]
    [InlineData(SwitchNote.RestoreFailed, "Note_RestoreFailed")]
    [InlineData(SwitchNote.ModesFromDatabase, "Note_ModesFromDatabase")]
    [InlineData(SwitchNote.DriverHung, "Note_DriverHung")]
    public void ForNotification_Notes_FollowTheText(SwitchNote note, string key)
    {
        (_, string text, _) = SwitchMessages.ForNotification(Record(SwitchOutcome.Failed) with { Note = note, NativeError = 31 });

        text.ShouldEndWith(Environment.NewLine + Loc.Instance[key]);
    }

    [Fact]
    public void ForNotification_AudioHdrWallpaperIconsAndApps_AddALineEach()
    {
        SwitchRecord record = Record(SwitchOutcome.Applied) with
        {
            Audio = AudioOutcome.Incomplete,
            Hdr = HdrOutcome.Incomplete,
            Wallpaper = WallpaperOutcome.FileMissing,
            DesktopIcons = DesktopIconOutcome.AutoArrange,
            Apps = AppsOutcome.DeviceMissing,
            AppsWaitDevice = "Wheel",
            AppsWaitSeconds = 30,
        };

        (_, string text, _) = SwitchMessages.ForNotification(record);

        text.Split(Environment.NewLine).ShouldBe(
        [
            Loc.Instance["Result_AppliedText"],
            Loc.Instance["Result_AudioIncomplete"],
            Loc.Instance["Result_HdrIncomplete"],
            Loc.Instance["Result_WallpaperMissing"],
            Loc.Instance["Result_IconsAutoArrange"],
            Loc.Format("Result_AppsDeviceMissing", "Wheel", 30),
        ]);
    }

    [Fact]
    public void ForFollowUpNotification_OnlyWhenAppsOrIconsWentWrong()
    {
        SwitchMessages.ForFollowUpNotification(Record(SwitchOutcome.Applied)).ShouldBeNull();

        (string Title, string Text, NoticeLevel Icon)? problem = SwitchMessages.ForFollowUpNotification(
            Record(SwitchOutcome.Applied) with { Apps = AppsOutcome.Incomplete, DesktopIcons = DesktopIconOutcome.Unavailable });

        problem.ShouldNotBeNull();
        problem.Value.Icon.ShouldBe(NoticeLevel.Warning);
        problem.Value.Text.ShouldBe(Loc.Instance["Result_IconsUnavailable"] + Environment.NewLine + Loc.Instance["Result_AppsIncomplete"]);
    }

    [Theory]
    [InlineData(AllDisplaysOnOutcome.TurnedOn, NoticeLevel.Info)]
    [InlineData(AllDisplaysOnOutcome.TurnedOnAt60Hz, NoticeLevel.Info)]
    [InlineData(AllDisplaysOnOutcome.AlreadyOn, NoticeLevel.Info)]
    [InlineData(AllDisplaysOnOutcome.NoDisplays, NoticeLevel.Warning)]
    public void ForAllDisplaysOn_EveryOutcome_HasItsText(AllDisplaysOnOutcome outcome, NoticeLevel expected)
    {
        (string title, string text, NoticeLevel icon) = SwitchMessages.ForAllDisplaysOn(new AllDisplaysOnReport(new AllDisplaysOnResult(outcome, 3, 0), null));

        title.ShouldBe(Loc.Instance["AllOn_Title"]);
        text.ShouldBe(Loc.Instance["AllOn_" + outcome]);
        icon.ShouldBe(expected);
    }

    [Fact]
    public void ForAllDisplaysOn_Failed_NamesTheProfileItFellBackTo()
    {
        (_, string withFallback, NoticeLevel icon) = SwitchMessages.ForAllDisplaysOn(
            new AllDisplaysOnReport(new AllDisplaysOnResult(AllDisplaysOnOutcome.Failed, 3, 0), "Desk"));
        (_, string without, _) = SwitchMessages.ForAllDisplaysOn(
            new AllDisplaysOnReport(new AllDisplaysOnResult(AllDisplaysOnOutcome.Failed, 3, 31), null));

        icon.ShouldBe(NoticeLevel.Error);
        withFallback.ShouldBe(Loc.Format("AllOn_FailedFallback", Loc.Instance["Result_FailedUnexpected"], "Desk"));
        without.ShouldStartWith(Loc.Format("AllOn_Failed", string.Empty).TrimEnd());
    }

    [Theory]
    [InlineData(VelopackLogLevel.Trace, LogEventLevel.Verbose)]
    [InlineData(VelopackLogLevel.Debug, LogEventLevel.Debug)]
    [InlineData(VelopackLogLevel.Information, LogEventLevel.Information)]
    [InlineData(VelopackLogLevel.Warning, LogEventLevel.Warning)]
    [InlineData(VelopackLogLevel.Error, LogEventLevel.Error)]
    [InlineData(VelopackLogLevel.Critical, LogEventLevel.Fatal)]
    public void VelopackLogger_KeepsTheLevel_AndTheMessageLiteral(VelopackLogLevel level, LogEventLevel expected)
    {
        var sink = new Sink();
        using Logger log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        new SerilogVelopackLogger(log).Log(level, @"C:\{braces}\RigShift", null);

        LogEvent logged = sink.Events.ShouldHaveSingleItem();
        logged.Level.ShouldBe(expected);
        logged.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldBe("\"C:\\{braces}\\RigShift\"");
    }

    [Fact]
    public void VelopackLogger_PortableStart_IsNoWarning()
    {
        var sink = new Sink();
        using Logger log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        new SerilogVelopackLogger(log).Log(VelopackLogLevel.Warning, "Failed to initialize WindowsVelopackLocator: not installed", null);

        sink.Events.ShouldHaveSingleItem().Level.ShouldBe(LogEventLevel.Debug);
    }

    private static SwitchRecord Record(SwitchOutcome outcome) =>
        new(DateTimeOffset.UtcNow, "Rig", outcome, AudioOutcome.NotConfigured, AppsOutcome.NotConfigured, 1, TimeSpan.FromSeconds(2), null, null, []);

    private sealed class Sink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
