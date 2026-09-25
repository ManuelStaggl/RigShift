using RigShift.App.Localization;

namespace RigShift.App.ViewModels;

/// <summary>The line under an editor field, from the problems the core found. The text key is "Problem_" + the problem's name.</summary>
internal static class ProblemTexts
{
    /// <summary>The texts of those <paramref name="problems"/> that belong to one field, or <c>null</c> when it has none.</summary>
    public static string? Of<TProblem>(IReadOnlyList<TProblem> problems, params TProblem[] kinds)
        where TProblem : struct, Enum
    {
        List<string> texts = kinds.Where(problems.Contains).Select(p => Loc.Instance["Problem_" + p]).ToList();
        return texts.Count == 0 ? null : string.Join(" ", texts);
    }
}
