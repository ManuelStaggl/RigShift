using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace RigShift.App.ViewModels;

/// <summary>
/// An editor's fields are its properties with a public setter, so a new field counts for validation and the dirty flag
/// as soon as it is bindable.
/// </summary>
internal static class EditorFields<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>
{
    private static readonly FrozenSet<string> Names = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.SetMethod is { IsPublic: true })
        .Select(p => p.Name)
        .ToFrozenSet(StringComparer.Ordinal);

    public static bool Contains(string? propertyName) => propertyName is not null && Names.Contains(propertyName);
}
