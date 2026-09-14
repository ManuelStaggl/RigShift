namespace RigShift.App.ViewModels;

public sealed record Choice(string? Key, string Name)
{
    /// <summary>Screen readers and type-ahead in combo boxes read the display name.</summary>
    public override string ToString() => Name;
}
