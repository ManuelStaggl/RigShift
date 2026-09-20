using System.Text;
using RigShift.Core.Storage;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private string Target => Path.Combine(_directory, "games.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<Stream, Task> Text(string text) => stream => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    [Fact]
    public async Task Write_CreatesTheFolderAndTheFile_AndLeavesNothingElseBehind()
    {
        await AtomicFile.WriteAsync(Target, Text("new"), Ct);

        (await File.ReadAllTextAsync(Target, Ct)).ShouldBe("new");
        Directory.GetFiles(_directory).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Write_ThatFailsHalfway_KeepsTheOldFileAndRemovesItsTemp()
    {
        await AtomicFile.WriteAsync(Target, Text("old"), Ct);

        await Should.ThrowAsync<InvalidOperationException>(() => AtomicFile.WriteAsync(
            Target,
            async stream =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("hal"), Ct);
                throw new InvalidOperationException("serializer gave up");
            },
            Ct));

        (await File.ReadAllTextAsync(Target, Ct)).ShouldBe("old");
        Directory.GetFiles(_directory).ShouldHaveSingleItem();
    }

    /// <summary>Two writers used to share one ".tmp": the second truncated what the first was about to move into place.</summary>
    [Fact]
    public async Task Write_FromTwoWritersAtOnce_NeverMixesTheirContent()
    {
        var firstIsWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondIsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = AtomicFile.WriteAsync(
            Target,
            async stream =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("first-"), Ct);
                firstIsWriting.SetResult();
                await secondIsDone.Task;
                await stream.WriteAsync(Encoding.UTF8.GetBytes("first"), Ct);
            },
            Ct);
        await firstIsWriting.Task;
        await AtomicFile.WriteAsync(Target, Text("second"), Ct);
        secondIsDone.SetResult();
        await first;

        (await File.ReadAllTextAsync(Target, Ct)).ShouldBe("first-first");
        Directory.GetFiles(_directory).ShouldHaveSingleItem();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
