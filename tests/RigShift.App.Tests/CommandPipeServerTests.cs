using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog;
using Serilog.Core;
using Serilog.Events;
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

    /// <summary>
    /// Somebody put a pipe of our name up first and left room for more instances. Joining it would put their access
    /// list in charge of who may send commands, so the server has to stay out until the name is free.
    /// </summary>
    [Fact]
    public async Task NameSquattedWithFreeInstances_IsNotJoined()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        var sink = new CollectingSink();
        using Logger log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var runner = new CommandRunner(new InMemoryProfileStore(), new FakeDisplayConfigurator(DeskActive()), Substitute.For<IAudioController>(),
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), Logger.None);

        var squatter = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var server = new CommandPipeServer(runner, Substitute.For<IAppShell>(), log, pipeName, work => work());
        server.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!sink.Events.Any(e => e.Level == LogEventLevel.Warning))
        {
            await Task.Delay(50, timeout.Token);
        }

        // Whoever connects now reaches the squatter's instance, not one of ours.
        await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            Task accepted = squatter.WaitForConnectionAsync(timeout.Token);
            await client.ConnectAsync(timeout.Token);
            await accepted;
        }

        await squatter.DisposeAsync();

        await using var second = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await second.ConnectAsync(timeout.Token);
        await PipeProtocol.WriteRequestAsync(second, new PipeRequest(["list"]), timeout.Token);
        (await PipeProtocol.ReadResponseAsync(second, timeout.Token)).ExitCode.ShouldBe(CliExitCodes.Applied);
    }

    [Fact]
    public async Task Trust_OurOwnServer_IsTrusted()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        var runner = new CommandRunner(new InMemoryProfileStore(), new FakeDisplayConfigurator(DeskActive()), Substitute.For<IAudioController>(),
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), Logger.None);
        using var server = new CommandPipeServer(runner, Substitute.For<IAppShell>(), Logger.None, pipeName, work => work());
        server.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);

        PipeTrust.BelongsToThisUser(client, out string reason).ShouldBeTrue(reason);
    }

    /// <summary>A pipe under our name that lets everybody in is not RigShift, whoever created it.</summary>
    [Fact]
    public async Task Trust_PipeOpenToEveryone_IsRefused()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        await using NamedPipeServerStream open = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task accepted = open.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accepted;

        PipeTrust.BelongsToThisUser(client, out string reason).ShouldBeFalse();
        reason.ShouldContain("S-1-1-0");
    }

    [Theory]
    [InlineData("RigShift.1", true)]
    [InlineData("RigShift.12", true)]
    [InlineData("RigShift.", false)]
    [InlineData("RigShift.!", false)]
    [InlineData("RigShift.1x", false)]
    [InlineData("RigShift.Tests.abc", false)]
    [InlineData("Other.1", false)]
    public void IsInstancePipe_AcceptsOnlySessionNumbers(string name, bool expected)
    {
        CommandLineClient.IsInstancePipe(name).ShouldBe(expected);
    }

    [Fact]
    public async Task SilentClient_IsDisconnectedAfterTheRequestTimeout()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        var sink = new CollectingSink();
        using Logger log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var store = new InMemoryProfileStore();
        store.Profiles.Add(Rig());
        var runner = new CommandRunner(store, new FakeDisplayConfigurator(DeskActive()), Substitute.For<IAudioController>(),
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), Logger.None);
        using var server = new CommandPipeServer(
            runner, Substitute.For<IAppShell>(), log, pipeName, work => work(), requestTimeout: TimeSpan.FromMilliseconds(300));
        server.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var silent = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await silent.ConnectAsync(timeout.Token);

        // The server closes its end: the read ends without data instead of waiting for the test's timeout.
        int read = await silent.ReadAsync(new byte[1], timeout.Token);

        read.ShouldBe(0);
        sink.Events.ShouldContain(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("no complete request", StringComparison.Ordinal));

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
