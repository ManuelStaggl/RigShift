using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigShift.Core.Automation;

/// <summary>A game RigShift recognizes by its process names. Built-in templates live in <c>templates/games</c>.</summary>
public sealed record GameTemplate
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Program file names, e.g. <c>AMS2AVX.exe</c>; any of them running counts as the game running.</summary>
    public required IReadOnlyList<string> Executables { get; init; }
}

/// <summary>Loads the templates embedded from <c>templates/games/*.json</c>.</summary>
public static class GameTemplates
{
    private const string ResourcePrefix = "RigShift.Templates.Games.";

    private static readonly Lazy<IReadOnlyList<GameTemplate>> BuiltInTemplates = new(LoadBuiltIn);

    /// <summary>All built-in templates, sorted by name.</summary>
    public static IReadOnlyList<GameTemplate> BuiltIn => BuiltInTemplates.Value;

    public static GameTemplate? Find(string? id) =>
        id is null ? null : BuiltIn.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public static GameTemplate Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, TemplateJsonContext.Default.GameTemplate)
            ?? throw new JsonException("A game template must not be null.");
    }

    /// <summary>Process names a rule watches: the template's programs or the custom program.</summary>
    public static IReadOnlyList<string> ProcessNamesOf(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.TemplateId is not null)
        {
            return Find(rule.TemplateId)?.Executables.Select(ProcessNames.Normalize).OfType<string>().ToList() ?? [];
        }

        return ProcessNames.Normalize(rule.ExecutablePath) is { } name ? [name] : [];
    }

    private static List<GameTemplate> LoadBuiltIn()
    {
        Assembly assembly = typeof(GameTemplates).Assembly;
        var templates = new List<GameTemplate>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded template {resource} is missing.");
            templates.Add(Parse(stream));
        }

        return templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Process names as Windows reports them: file name without <c>.exe</c>, compared without case.</summary>
public static class ProcessNames
{
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary><c>"C:\Games\LMU\Le Mans Ultimate.exe"</c> → <c>Le Mans Ultimate</c>; <c>null</c> for an empty value.</summary>
    public static string? Normalize(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return null;
        }

        string name = Path.GetFileName(Environment.ExpandEnvironmentVariables(pathOrName.Trim().Trim('"')).TrimEnd('\\', '/'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length == 0 ? null : name;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(GameTemplate))]
internal sealed partial class TemplateJsonContext : JsonSerializerContext;
