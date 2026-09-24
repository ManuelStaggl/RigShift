using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RigShift.App.Services;
using RigShift.App.Views;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>
/// The tray app's container as the app registers it. A missing registration used to show only when the user opened the
/// page that needed it (A-11); nothing here builds a window.
/// </summary>
public sealed class ServiceRegistrationTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-app-tests", Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Build_FindsEveryConstructorParameter_AndNoCycle() =>
        Should.NotThrow(() =>
        {
            using ServiceProvider provider = Services().BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        });

    [Fact]
    public void EveryPageOfTheNavigationRail_IsRegistered()
    {
        using ServiceProvider provider = Services().BuildServiceProvider();
        IServiceProviderIsService registered = provider.GetRequiredService<IServiceProviderIsService>();

        MainWindow.Pages.ShouldNotBeEmpty();
        MainWindow.Pages.Select(p => p.Page).Where(page => !registered.IsService(page)).ShouldBeEmpty();
    }

    [Fact]
    public void HandWiredRegistrations_FindTheirParts()
    {
        // The validation cannot look inside a factory, and these two ask the container for their parts themselves.
        using ServiceProvider provider = Services().BuildServiceProvider();

        provider.GetRequiredService<CommandRunner>().ShouldNotBeNull();
        provider.GetRequiredService<Func<GameSessionRunner>>()().ShouldNotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    private IServiceCollection Services() =>
        new ServiceCollection().AddRigShift(_paths, Substitute.For<IAppShell>(), Logger.None);
}
