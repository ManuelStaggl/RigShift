using RigShift.App.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>The real session watch and the shell helper, as far as they go without a lock screen or a browser.</summary>
public sealed class SystemSessionWatchTests
{
    [Fact]
    public void NewWatch_IsInteractive_AndLetsGoOfTheSystemEvent()
    {
        var watch = new SystemSessionWatch(Logger.None);

        watch.IsInteractive.ShouldBeTrue();

        watch.Dispose();
        watch.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("http://example.com")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    public void OpenUrl_AnythingButHttps_IsRefusedAndLogged(string? url)
    {
        var sink = new Sink();
        using Logger log = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        ShellFolders.OpenUrl(url, log);

        sink.Events.ShouldHaveSingleItem().Level.ShouldBe(LogEventLevel.Warning);
    }

    private sealed class Sink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
