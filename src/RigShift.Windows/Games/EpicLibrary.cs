using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Storage;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// Installed Epic games from <c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item</c> – plain JSON, one file
/// per game, holding the title, the install folder, the executable and the <c>AppName</c> the launcher URI needs.
/// Epic is the friendly case: the real executable is named, so the process name never has to be learned.
/// </summary>
public sealed class EpicLibrary
{
    private readonly ILogger _log;
    private readonly string _manifestFolder;

    /// <param name="manifestFolder">Folder holding the <c>.item</c> files; the default location when omitted.</param>
    public EpicLibrary(ILogger log, string? manifestFolder = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<EpicLibrary>();
        _manifestFolder = manifestFolder ?? DefaultManifestFolder;
    }

    public static string DefaultManifestFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public IReadOnlyList<InstalledGame> Find()
    {
        if (!Directory.Exists(_manifestFolder))
        {
            _log.Information("Epic is not installed, or {Folder} is gone", _manifestFolder);
            return [];
        }

        var games = new List<InstalledGame>();
        foreach (string file in Directory.EnumerateFiles(_manifestFolder, "*.item"))
        {
            EpicManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(BoundedRead.Text(file), EpicJsonContext.Default.EpicManifest);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.Debug(ex, "Epic manifest {File} could not be read, skipped", file);
                continue;
            }

            if (manifest is null || string.IsNullOrEmpty(manifest.AppName) || string.IsNullOrEmpty(manifest.DisplayName))
            {
                continue;
            }

            games.Add(new InstalledGame(manifest.DisplayName, new GameLaunch
            {
                Kind = GameLaunchKind.Epic,
                Target = manifest.AppName,
                InstallFolder = Directory.Exists(manifest.InstallLocation) ? manifest.InstallLocation : null,
                ProcessName = ProcessNameOf(manifest.LaunchExecutable),
            }));
        }

        _log.Information("Epic: {Count} installed games", games.Count);
        return games;
    }

    /// <summary><c>LaunchExecutable</c> is relative to the install folder, e.g. <c>Binaries\Win64\Game.exe</c>.</summary>
    public static string? ProcessNameOf(string? launchExecutable) => string.IsNullOrWhiteSpace(launchExecutable)
        ? null
        : Path.GetFileNameWithoutExtension(launchExecutable.Replace('/', '\\'));
}

/// <summary>The fields of a <c>.item</c> manifest we use; Epic writes many more.</summary>
internal sealed record EpicManifest(string? AppName, string? DisplayName, string? InstallLocation, string? LaunchExecutable);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EpicManifest))]
internal sealed partial class EpicJsonContext : JsonSerializerContext;
