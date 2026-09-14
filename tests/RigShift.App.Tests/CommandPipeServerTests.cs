using System.Collections.Concurrent;
using System.IO.Pipes;
using NSubstitute;
using Serilog;
using Serilog.Events;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class CommandPipeServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RoundTrip_ListOverRealPipe()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        var store = new InMemoryProfileStore();
        store.Profiles.Add(Rig());
        var runner = new CommandRunner(store, new FakeDisplayConfigurator(DeskActive()), Substitute.For<IAudioController>(),
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), Logger.None);
        using var server = new CommandPipeServer(runner, Substitute.For<IAppShell>(), Logger.None, pipeName, work => work());
        server.Start();

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        await PipeProtocol.WriteRequestAsync(client, new PipeRequest(["list"]), timeout.Token);
        PipeResponse response = await PipeProtocol.ReadResponseAsync(client, timeout.Token);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldContain("Rig");
    }

    [Fact]
    public async Task NameTaken_LogsWarningAndServesOnceFree()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        var sink = new CollectingSink();
        using Logger log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var store = new InMemoryProfileStore();
        store.Profiles.Add(Rig());
        var runner = new CommandRunner(store, new FakeDisplayConfigurator(DeskActive()), Substitute.For<IAudioController>(),
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), Logger.None);

        // Someone else owns the name with a single instance: the server cannot create its own.
        var blocker = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var server = new CommandPipeServer(runner, Substitute.For<IAppShell>(), log, pipeName, work => work());
        server.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!sink.Events.Any(e => e.Level == LogEventLevel.Warning))
        {
            await Task.Delay(50, timeout.Token);
        }

        sink.Events.ShouldNotContain(e => e.Level >= LogEventLevel.Error);
        await blocker.DisposeAsync();

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        await PipeProtocol.WriteRequestAsync(client, new PipeRequest(["list"]), timeout.Token);
        PipeResponse response = await PipeProtocol.ReadResponseAsync(client, timeout.Token);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
