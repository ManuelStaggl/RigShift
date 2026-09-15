using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RigShift.App.Localization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>Where RigShift keeps its files: <c>%AppData%\RigShift</c>.</summary>
public sealed record AppPaths(string DataDirectory)
{
    public string Profiles => Path.GetFullPath(Path.Combine(DataDirectory, "profiles"));

    public string Logs => Path.GetFullPath(Path.Combine(DataDirectory, "logs"));

    public string SettingsFile => Path.GetFullPath(Path.Combine(DataDirectory, "settings.json"));
}

/// <summary>Process-level actions the UI needs without knowing the <see cref="App"/> class.</summary>
public interface IAppShell
{
    bool IsExiting { get; }

    void ShowMainWindow(Type? page = null);

    void Quit();
}

/// <summary>
/// Current settings plus change notification. Language changes take effect immediately. Updates are serialized, so the
/// switch thread (ducking memory) and the UI never overwrite each other's change.
/// </summary>
public sealed class SettingsService(JsonSettingsStore store, IAutostart autostart) : IDisposable
{
    private readonly SemaphoreSlim _updates = new(1, 1);

    /// <summary>Raised on the thread of the update; only updates with <c>notify</c> raise it (those come from the UI thread).</summary>
    public event EventHandler? Changed;

    public AppSettings Current { get; private set; } = new();

    public IAutostart Autostart => autostart;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Current = await store.LoadAsync(cancellationToken);
        Loc.Instance.SetLanguage(Current.Language);
    }

    /// <summary>The file was replaced behind our back (a restored backup): read it again and tell everyone. UI thread.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _updates.WaitAsync(cancellationToken);
        try
        {
            Current = await store.LoadAsync(cancellationToken);
        }
        finally
        {
            _updates.Release();
        }

        Loc.Instance.SetLanguage(Current.Language);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <param name="notify">
    /// <c>false</c> for bookkeeping the UI does not show (ducking memory): no <see cref="Changed"/>, so it is safe off the
    /// UI thread and does not rebuild pages on every switch.
    /// </param>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _updates.WaitAsync(cancellationToken);
        bool languageChanged;
        try
        {
            AppSettings updated = change(Current);
            if (updated == Current)
            {
                return;
            }

            // Saved first: if writing fails, Current keeps the value that is really on disk (analysis finding F-01).
            await store.SaveAsync(updated, cancellationToken);
            languageChanged = !string.Equals(updated.Language, Current.Language, StringComparison.Ordinal);
            Current = updated;
        }
        finally
        {
            _updates.Release();
        }

        if (!notify)
        {
            return;
        }

        if (languageChanged)
        {
            Loc.Instance.SetLanguage(Current.Language);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _updates.Dispose();
}

/// <summary>Keeps the communications ducking memory in <c>settings.json</c>.</summary>
public sealed class SettingsDuckingMemory(SettingsService settings) : IDuckingMemory
{
    public Task<RememberedDucking?> LoadAsync(CancellationToken cancellationToken)
    {
        AppSettings current = settings.Current;
        return Task.FromResult(current.HasDuckingMemory ? new RememberedDucking(current.DuckingBeforeProfiles) : null);
    }

    public Task SaveAsync(int? value, CancellationToken cancellationToken) =>
        settings.UpdateAsync(s => s with { HasDuckingMemory = true, DuckingBeforeProfiles = value }, cancellationToken, notify: false);

    public Task ClearAsync(CancellationToken cancellationToken) =>
        settings.UpdateAsync(s => s with { HasDuckingMemory = false, DuckingBeforeProfiles = null }, cancellationToken, notify: false);
}

/// <summary>One finished switch, for the tray notification and the diagnostics history.</summary>
public sealed record SwitchRecord(
    DateTimeOffset At,
    string ProfileName,
    SwitchOutcome Outcome,
    AudioOutcome Audio,
    AppsOutcome Apps,
    int Attempts,
    TimeSpan Duration,
    int? NativeError,
    string? Message,
    IReadOnlyList<string> MissingDisplays,
    string? AppsWaitDevice = null,
    int AppsWaitSeconds = 0,
    SwitchNote Note = SwitchNote.None)
{
    public string OutcomeText => SwitchMessages.Outcome(Outcome);

    public string Details => Message ?? string.Join(", ", MissingDisplays);

    public bool HasDetails => !string.IsNullOrEmpty(Details);

    public string TimeText => At.ToLocalTime().ToString("T", Loc.Instance.Culture);

    public string DurationText => Loc.Format("About_Duration", Duration.TotalSeconds.ToString("0.0", Loc.Instance.Culture));

    public Wpf.Ui.Controls.SymbolRegular Symbol => Outcome switch
    {
        SwitchOutcome.Applied or SwitchOutcome.AppliedPartially => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
        SwitchOutcome.RolledBack => Wpf.Ui.Controls.SymbolRegular.ArrowUndo24,
        SwitchOutcome.Blocked => Wpf.Ui.Controls.SymbolRegular.Warning24,
        SwitchOutcome.DryRun => Wpf.Ui.Controls.SymbolRegular.Eye24,
        _ => Wpf.Ui.Controls.SymbolRegular.ErrorCircle24,
    };
}

/// <summary>User-facing texts for switch results and plans.</summary>
public static class SwitchMessages
{
    public static string Outcome(SwitchOutcome outcome) => Loc.Instance["Outcome_" + outcome];

    /// <summary>"Left · CM27X3", or the model alone without a custom name.</summary>
    public static string NameOf(string? customName, DisplayIdentity identity) =>
        DisplayNames.Label(customName, identity, Loc.Instance["Display_Unnamed"]);

    public static string NameOf(DisplayAssignment display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return NameOf(display.CustomName, display.Identity);
    }

    public static string DescribePlan(TopologyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var lines = new List<string>();
        if (plan.IsBlocked)
        {
            lines.Add(Loc.Format("Check_Blocked", Names(plan.Missing.Where(m => !m.Assignment.IsOptional))));
        }
        else if (plan.Missing.Count > 0)
        {
            lines.Add(Loc.Format("Check_OptionalMissing", Names(plan.Missing)));
        }
        else
        {
            lines.Add(Loc.Instance["Check_Ready"]);
        }

        lines.AddRange(plan.Warnings.Select(w => w.Kind).Distinct().Select(kind => Loc.Instance["Warning_" + kind]));
        return string.Join(Environment.NewLine, lines);
    }

    public static (string Title, string Text, H.NotifyIcon.Core.NotificationIcon Icon) ForNotification(SwitchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string missing = string.Join(", ", record.MissingDisplays);
        (string title, string text, H.NotifyIcon.Core.NotificationIcon icon) = record.Outcome switch
        {
            // Only optional displays are missing: switched, and they follow once connected (finding HW-03).
            SwitchOutcome.Applied when record.MissingDisplays.Count > 0 =>
                (Loc.Format("Result_AppliedTitle", record.ProfileName), Loc.Format("Result_AppliedFollowUpText", missing), H.NotifyIcon.Core.NotificationIcon.Info),
            SwitchOutcome.Applied => (Loc.Format("Result_AppliedTitle", record.ProfileName), Loc.Instance["Result_AppliedText"], H.NotifyIcon.Core.NotificationIcon.Info),
            SwitchOutcome.AppliedPartially => (Loc.Format("Result_AppliedTitle", record.ProfileName), Loc.Format("Result_PartialText", missing), H.NotifyIcon.Core.NotificationIcon.Info),
            SwitchOutcome.RolledBack => (Loc.Format("Result_RolledBackTitle", record.ProfileName), Loc.Instance["Result_RolledBackText"], H.NotifyIcon.Core.NotificationIcon.Warning),
            SwitchOutcome.Blocked => (Loc.Format("Result_BlockedTitle", record.ProfileName), Loc.Format("Result_BlockedText", missing), H.NotifyIcon.Core.NotificationIcon.Warning),
            _ => (Loc.Format("Result_FailedTitle", record.ProfileName),
                record.NativeError is { } code ? Loc.Format("Result_FailedText", code) : Loc.Instance["Result_FailedUnexpected"],
                H.NotifyIcon.Core.NotificationIcon.Error),
        };

        // Rolled back already says the previous settings are back.
        string? note = record.Note switch
        {
            SwitchNote.RestoredPrevious when record.Outcome != SwitchOutcome.RolledBack => Loc.Instance["Note_RestoredPrevious"],
            SwitchNote.RestoreFailed => Loc.Instance["Note_RestoreFailed"],
            SwitchNote.ModesFromDatabase => Loc.Instance["Note_ModesFromDatabase"],
            _ => null,
        };
        if (note is not null)
        {
            text += Environment.NewLine + note;
        }

        if (record.Audio == AudioOutcome.Incomplete && record.Outcome is SwitchOutcome.Applied or SwitchOutcome.AppliedPartially)
        {
            text += Environment.NewLine + Loc.Instance["Result_AudioIncomplete"];
        }

        if (AppsProblem(record) is { } apps)
        {
            text += Environment.NewLine + apps;
        }

        return (title, text, icon);
    }

    /// <summary>Apps run after the switch result (B-03): a second notification only when something went wrong with them.</summary>
    public static (string Title, string Text, H.NotifyIcon.Core.NotificationIcon Icon)? ForAppsNotification(SwitchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return AppsProblem(record) is { } text
            ? (Loc.Format("Result_AppliedTitle", record.ProfileName), text, H.NotifyIcon.Core.NotificationIcon.Warning)
            : null;
    }

    private static string? AppsProblem(SwitchRecord record) => record.Apps switch
    {
        AppsOutcome.Incomplete => Loc.Instance["Result_AppsIncomplete"],
        AppsOutcome.DeviceMissing => Loc.Format("Result_AppsDeviceMissing", record.AppsWaitDevice ?? string.Empty, record.AppsWaitSeconds),
        _ => null,
    };

    private static string Names(IEnumerable<MissingDisplay> missing) =>
        string.Join(", ", missing.Select(m => NameOf(m.Assignment)));
}

/// <summary>Opens folders in Explorer and web pages in the default browser.</summary>
public static class ShellFolders
{
    public static void OpenUrl(string? url, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            log.Warning("Not opening {Url}: not an https address", url);
            return;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.Warning(ex, "Could not open {Url}", uri);
        }
    }

    public static void Open(string path, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            Directory.CreateDirectory(path);
            using Process? process = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            log.Warning(ex, "Could not open folder {Folder}", path);
        }
    }
}
