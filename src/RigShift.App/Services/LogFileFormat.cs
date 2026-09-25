using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RigShift.Windows.Display;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace RigShift.App.Services;

/// <summary>
/// The head the tray app writes at its start and on every new day. It runs for days and the file rolls daily, so
/// "today's log" in a forum post would otherwise hold neither version nor Windows build nor graphics driver (v4 finding
/// A-17). Serilog's file hooks would do this per file, but they refuse shared files – and the tray app and command line
/// processes share one.
/// </summary>
public static class LogFileHeader
{
    private static readonly Lazy<string> Machine = new(DescribeMachine);

    /// <summary>Only the tray app writes the head: a command line process lives for a second and would repeat it.</summary>
    public static bool Enabled { get; set; }

    /// <summary>What the running app knows beyond the machine: displays and active profile. <c>null</c> until it knows.</summary>
    public static Func<string?>? AppState { get; set; }

    public static string Text()
    {
        var text = new StringBuilder(Machine.Value);
        string? state = null;
        try
        {
            state = AppState?.Invoke();
        }
        catch (InvalidOperationException)
        {
            // The app is mid-change on another thread; the head goes out without it rather than not at all.
        }

        if (state is not null)
        {
            text.Append("# ").Append(state).AppendLine();
        }

        return text.ToString();
    }

    private static string DescribeMachine()
    {
        var text = new StringBuilder();
        text.AppendLine(FormattableString.Invariant(
            $"# RigShift {typeof(LogFileHeader).Assembly.GetName().Version} · Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture}) · .NET {Environment.Version}"));
        foreach (GraphicsDriver driver in GraphicsDrivers.Read())
        {
            text.Append("# GPU: ").Append(driver).AppendLine();
        }

        return text.ToString();
    }
}

/// <summary>
/// Writes each line with the user's profile folder shortened to <c>%AppData%</c>, <c>%LocalAppData%</c> or
/// <c>%UserProfile%</c>: whoever pastes a log into an issue would otherwise publish their Windows user name with every
/// path (v4 finding E-17). Works on the finished line, so paths in exception texts are covered too. Puts
/// <see cref="LogFileHeader"/> in front of the first line of each day.
/// </summary>
public sealed class LogLineFormatter(string outputTemplate) : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _inner = new(outputTemplate, CultureInfo.InvariantCulture);
    private readonly Lock _gate = new();
    private DateTime _headerDay;

    private readonly (string Path, string Name)[] _folders =
    [
        .. new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%AppData%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LocalAppData%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%UserProfile%"),
        }.Where(f => f.Item1.Length > 3)
        // Some callers build paths with forward slashes.
        .SelectMany(f => new[] { f, (f.Item1.Replace('\\', '/'), f.Item2) }),
    ];

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        if (LogFileHeader.Enabled && NewDay(logEvent.Timestamp.LocalDateTime.Date))
        {
            output.Write(Shorten(LogFileHeader.Text()));
        }

        using var line = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, line);
        output.Write(Shorten(line.ToString()));
    }

    public string Shorten(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // AppData first: it lies inside the profile folder.
        foreach ((string path, string name) in _folders)
        {
            text = text.Replace(path, name, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private bool NewDay(DateTime day)
    {
        lock (_gate)
        {
            if (day == _headerDay)
            {
                return false;
            }

            _headerDay = day;
            return true;
        }
    }
}
