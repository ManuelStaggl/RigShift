using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Storage;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// PC games from the Xbox app (v4 finding U-07): each lies in <c>&lt;drive&gt;:\XboxGames\&lt;game&gt;\Content</c> with a
/// <c>MicrosoftGame.config</c> that names it, its package identity and its executable. It starts through
/// <c>shell:AppsFolder</c> with its app user model id – the package cannot be started through its files.
/// </summary>
public sealed class XboxLibrary
{
    private readonly ILogger _log;
    private readonly Func<IEnumerable<string>> _roots;

    /// <param name="roots">The <c>XboxGames</c> folders to look in; every fixed drive's when omitted.</param>
    public XboxLibrary(ILogger log, Func<IEnumerable<string>>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<XboxLibrary>();
        _roots = roots ?? DefaultRoots;
    }

    public IReadOnlyList<InstalledGame> Find()
    {
        var games = new List<InstalledGame>();
        foreach (string root in _roots().Where(Directory.Exists))
        {
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                string content = Path.Combine(folder, "Content");
                string config = Path.Combine(content, "MicrosoftGame.config");
                if (!File.Exists(config))
                {
                    continue;
                }

                try
                {
                    if (Parse(BoundedRead.Text(config), content, Path.GetFileName(folder)) is { } game)
                    {
                        games.Add(game);
                    }
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
                {
                    _log.Debug(ex, "Xbox game config {File} could not be read, skipped", config);
                }
            }
        }

        _log.Information("Xbox app: {Count} installed games", games.Count);
        return games;
    }

    /// <summary>The game a <c>MicrosoftGame.config</c> describes, or <c>null</c> when it lacks the identity or a PC executable.</summary>
    /// <param name="folderName">The game's folder, the name when the config only points into its resources.</param>
    public static InstalledGame? Parse(string xml, string contentFolder, string folderName)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, settings);
        XElement? root = XDocument.Load(reader).Root;
        XElement? identity = Child(root, "Identity");
        string? package = (string?)identity?.Attribute("Name");
        string? publisher = (string?)identity?.Attribute("Publisher");
        XElement? executable = Child(Child(root, "ExecutableList"), "Executable", e =>
            (string?)e.Attribute("TargetDeviceFamily") is null or "PC");
        string? appId = (string?)executable?.Attribute("Id");
        if (package is not { Length: > 0 } || publisher is not { Length: > 0 } || appId is not { Length: > 0 })
        {
            return null;
        }

        string? shown = (string?)Child(root, "ShellVisuals")?.Attribute("DefaultDisplayName");
        string name = shown is { Length: > 0 } && !shown.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? shown : folderName;

        // A launch helper starts the real executable; only a real one says which process is the game.
        string? process = Path.GetFileNameWithoutExtension((string?)executable!.Attribute("Name"));
        return new InstalledGame(name, new GameLaunch
        {
            Kind = GameLaunchKind.Xbox,
            Target = $"{package}_{PublisherId(publisher)}!{appId}",
            InstallFolder = contentFolder,
            ProcessName = process is { Length: > 0 } && !process.Equals("GameLaunchHelper", StringComparison.OrdinalIgnoreCase) ? process : null,
        });
    }

    /// <summary>
    /// The 13 characters of a package family name that stand for the publisher: the first 8 bytes of the SHA-256 of the
    /// publisher (UTF-16), as 13 groups of 5 bits in Crockford-like base 32 – "8wekyb3d8bbwe" for Microsoft.
    /// </summary>
    public static string PublisherId(string publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        byte[] hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (int i = 0; i < 8; i++)
        {
            bits = (bits << 8) | hash[i];
        }

        // 64 bits and one zero bit make 13 groups of five.
        var id = new StringBuilder(13);
        for (int group = 0; group < 13; group++)
        {
            int shift = 64 - ((group + 1) * 5);
            int value = shift >= 0 ? (int)((bits >> shift) & 0x1F) : (int)((bits << -shift) & 0x1F);
            id.Append(Alphabet[value]);
        }

        return id.ToString();
    }

    private static XElement? Child(XElement? parent, string name, Func<XElement, bool>? where = null) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name && (where is null || where(e)));

    private static IEnumerable<string> DefaultRoots() => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        .Select(d => Path.Combine(d.RootDirectory.FullName, "XboxGames"));
}
