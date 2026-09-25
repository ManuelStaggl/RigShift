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
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>
/// The command line side of the bridge that Stream Deck keys, desktop shortcuts and SSH commands go through (v4 finding
/// E-05): which instance it talks to, and what it reports when nobody answers.
/// </summary>
public sealed class CommandLineClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(new[] { "RigShift.2", "RigShift.1" }, "RigShift.1", "RigShift.1")]
    [InlineData(new[] { "RigShift.2", "RigShift.3" }, "RigShift.0", "RigShift.2")]
    [InlineData(new[] { "RigShift.Tests.x", "RigShiftX.1", "other" }, "RigShift.1", null)]
    [InlineData(new string[0], "RigShift.1", null)]
    public void Choose_OwnSessionFirst_ThenAnyOtherInstance(string[] pipes, string own, string? expected) =>
        CommandLineClient.Choose(pipes, own).ShouldBe(expected);

    [Fact]
    public async Task Send_ReturnsTheAppsAnswer()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        var store = new InMemoryProfileStore();
        store.Profiles.Add(Rig());
        var runner = new CommandRunner(store, new FakeDisplayConfigurator(DeskActive()), Substitute.For<IAudioController>(),
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), Logger.None);
        using var server = new CommandPipeServer(runner, Substitute.For<IAppShell>(), Logger.None, pipeName, work => work());
        server.Start();

        PipeResponse? response = await CommandLineClient.SendAsync(pipeName, ["list"], Wait);

        response.ShouldNotBeNull();
        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldContain("Rig");
    }

    [Fact]
    public async Task Send_NobodyListening_GivesUpAfterTheTimeout()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");

        PipeResponse? response = await CommandLineClient.SendAsync(pipeName, ["list"], TimeSpan.FromMilliseconds(200));

        response.ShouldBeNull();
    }

    /// <summary>An app that ends in the middle of a request (crash, update restart) must not leave the caller hanging.</summary>
    [Fact]
    public async Task Send_AppHangsUpWithoutAnswer_ReturnsNull()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        await using NamedPipeServerStream server = OwnPipe(pipeName);
        Task hangUp = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            await PipeProtocol.ReadRequestAsync(server, TestContext.Current.CancellationToken);
            server.Disconnect();
        }, TestContext.Current.CancellationToken);

        PipeResponse? response = await CommandLineClient.SendAsync(pipeName, ["list"], Wait);

        await hangUp;
        response.ShouldBeNull();
    }

    /// <summary>A pipe under our name that others may open could be anyone's: not a single argument goes out.</summary>
    [Fact]
    public async Task Send_PipeThatLetsOthersIn_SendsNothing()
    {
        string pipeName = "RigShift.Tests." + Guid.NewGuid().ToString("N");
        await using var squatter = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task<int> received = Task.Run(async () =>
        {
            await squatter.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            return await squatter.ReadAsync(new byte[16], TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        PipeResponse? response = await CommandLineClient.SendAsync(pipeName, ["apply", "Rig"], Wait);

        response.ShouldNotBeNull();
        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        (await received).ShouldBe(0, "the client hung up without writing");
    }

    /// <summary>A pipe as the tray app makes it: only this user may open it.</summary>
    private static NamedPipeServerStream OwnPipe(string name)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
}
