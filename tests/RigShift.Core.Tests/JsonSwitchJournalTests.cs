using System.Text.Json;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class JsonSwitchJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rigshift-journal-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string File => Path.Combine(_directory, JsonSwitchJournal.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Read_WithoutFile_ReturnsNothing()
    {
        (await Journal().ReadAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task BeginThenRead_ReturnsTheRecordedSwitch()
    {
        JsonSwitchJournal journal = Journal();
        await journal.BeginAsync(Entry(), Ct);

        InterruptedSwitch? read = await journal.ReadAsync(Ct);

        read.ShouldNotBeNull();
        read.TargetProfileName.ShouldBe("Rig");
        read.StartedUtc.ShouldBe(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        read.Previous.Displays.Count.ShouldBe(1);
        read.Previous.Displays[0].Identity.TargetDevicePath.ShouldBe("desk");
    }

    [Fact]
    public async Task Begin_Twice_KeepsOnlyTheLastSwitch()
    {
        JsonSwitchJournal journal = Journal();
        await journal.BeginAsync(Entry(), Ct);
        await journal.BeginAsync(Entry() with { TargetProfileName = "Desk" }, Ct);

        (await journal.ReadAsync(Ct))!.TargetProfileName.ShouldBe("Desk");
    }

    [Fact]
    public async Task Clear_RemovesTheRecordAndIsFineWithoutOne()
    {
        JsonSwitchJournal journal = Journal();
        await journal.BeginAsync(Entry(), Ct);

        await journal.ClearAsync(Ct);
        await journal.ClearAsync(Ct);

        System.IO.File.Exists(File).ShouldBeFalse();
        (await journal.ReadAsync(Ct)).ShouldBeNull();
    }

    /// <summary>Offering to restore a layout we cannot read would be worse than offering nothing.</summary>
    [Fact]
    public async Task Read_BrokenFile_ReturnsNothingInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, "{ not json", Ct);

        (await Journal().ReadAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Read_NewerSchema_ReturnsNothing()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(
            File,
            JsonSerializer.Serialize(new { schemaVersion = JsonSwitchJournal.CurrentSchemaVersion + 1, @switch = new { previous = new { id = Guid.Empty, name = "x", displays = Array.Empty<object>() }, targetProfileName = "Rig", startedUtc = "2026-09-16T12:00:00+00:00" } }),
            Ct);

        (await Journal().ReadAsync(Ct)).ShouldBeNull();
    }

    private JsonSwitchJournal Journal() => new(_directory, Logger.None);

    private static InterruptedSwitch Entry() => new()
    {
        Previous = new Profile
        {
            Id = Guid.Empty,
            Name = "Previous topology",
            Displays =
            [
                new DisplayAssignment
                {
                    Identity = new DisplayIdentity { AdapterDevicePath = "adapter", TargetDevicePath = "desk" },
                    Width = 3840,
                    Height = 2160,
                    RefreshNumerator = 60000,
                    RefreshDenominator = 1000,
                    PositionX = 0,
                    PositionY = 0,
                    IsOptional = true,
                },
            ],
            SwitchWithoutAsking = true,
        },
        TargetProfileName = "Rig",
        StartedUtc = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero),
    };
}
