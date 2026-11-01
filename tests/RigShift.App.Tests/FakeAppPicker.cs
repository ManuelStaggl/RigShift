using RigShift.App.ViewModels;

namespace RigShift.App.Tests;

/// <summary>An app picker that answers from a script: each call takes the next answer, <c>null</c> is "cancelled".</summary>
internal sealed class FakeAppPicker : IAppPicker
{
    public Queue<PickedApp?> Answers { get; } = new();

    /// <summary>The path each call started from; <c>null</c> for a new entry.</summary>
    public List<string?> OpenedWith { get; } = [];

    public PickedApp? Pick(string? currentPath)
    {
        OpenedWith.Add(currentPath);
        return Answers.Count > 0 ? Answers.Dequeue() : null;
    }
}
