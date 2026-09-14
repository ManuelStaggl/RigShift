using System.IO.Pipes;
using NSubstitute;
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
}
