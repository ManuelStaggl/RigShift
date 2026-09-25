using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace RigShift.App.ViewModels;

/// <summary>
/// The fields of an editor: its properties with a public setter, i.e. what the view or a command can change. The editors
/// recalculate validation and the dirty flag when one of them changes, so a new field counts as soon as it is bindable.
/// A hand-kept list of names was a trap: a field missing from it never made the editor dirty, and leaving the page
/// dropped the change without asking. What an editor works out itself (problems, dirty flag, texts) has a private setter.
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
