using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>Keeps every switch, like the WinUI build before its countdown window. The spike never starts one.</summary>
internal sealed class KeepSwitchConfirmation : ISwitchConfirmation
{
    public Task<ConfirmationResult> ConfirmAsync(Profile profile, DisplaySnapshot before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Log.Warning("No countdown in the spike, the switch to {Profile} is kept", profile?.Name);
        return Task.FromResult(ConfirmationResult.Confirmed);
    }
}

/// <summary>shortcut: no clipboard in the spike, the copy button only logs; Avalonia's TopLevel.Clipboard when the app is built.</summary>
internal sealed class LogClipboard : ITextClipboard
{
    public void SetText(string text) => Log.Information("Spike clipboard: {Text}", text);
}

/// <summary>shortcut: no app picker window in the spike; "Add app" does nothing.</summary>
internal sealed class NoAppPicker : IAppPicker
{
    public Task<PickedApp?> PickAsync(string? currentPath) => Task.FromResult<PickedApp?>(null);
}

/// <summary>shortcut: no global hotkeys in the spike.</summary>
internal sealed class NoHotkeys : IHotkeyRegistrar
{
    public event EventHandler<int>? Pressed
    {
        add { }
        remove { }
    }

    public bool Register(int id, Hotkey hotkey, out int win32Error)
    {
        win32Error = 0;
        return true;
    }

    public void Unregister(int id)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>The builders through the shared factory; every question answers "cancel", so nothing is deleted or discarded.</summary>
internal sealed class SpikeDialogs(ProfileEditorFactory factory) : IProfilePageDialogs, IHelpPageDialogs
{
    public Task<Profile> NewFromCurrentAsync() => factory.NewFromCurrentAsync();

    public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew) => factory.CreateEditorAsync(profile, isNew);

    public Task ShowSetupAssistantAsync() => Task.CompletedTask;

    public Task<bool> ConfirmDeleteAsync(string name, int ruleCount) => Task.FromResult(false);

    public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName) => Task.FromResult(UnsavedChoice.Discard);

    public string? PickZipToSave(string suggestedName) => null;

    public string? PickZipToOpen() => null;

    public Task<bool> ConfirmRestoreAsync(BackupContent content) => Task.FromResult(false);
}

/// <summary>
/// The spike's display configurator: every display of every profile reads as attached, the displays of
/// RIGSHIFT_PREVIEW_ACTIVE active in that profile's arrangement. Apply and HDR do nothing, so even a stray click on
/// "Switch" cannot change this machine's displays (a copy of the WPF app's debug PreviewDisplayConfigurator).
/// </summary>
internal sealed class PreviewDisplays(IProfileStore store, string? activeProfile) : IDisplayConfigurator
{
    public async Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        LoadResult loaded = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        Profile? active = loaded.Profiles.FirstOrDefault(p => string.Equals(p.Name, activeProfile, StringComparison.OrdinalIgnoreCase));
        List<AttachedDisplay> displays = [];
        int number = 1;
        foreach (DisplayAssignment assignment in loaded.Profiles.SelectMany(p => p.Displays))
        {
            if (displays.Exists(d => d.Identity == assignment.Identity))
            {
                continue;
            }

            DisplayAssignment? mode = active?.Displays.FirstOrDefault(d => d.Identity == assignment.Identity);
            displays.Add(new AttachedDisplay
            {
                Identity = assignment.Identity,
                IsAvailable = true,
                IsActive = mode is not null,
                ActiveMode = mode,
                WindowsNumber = mode is null ? null : number++,
                NativeHandle = new object(),
            });
        }

        return new DisplaySnapshot { TakenAt = DateTimeOffset.Now, Displays = displays };
    }

    public Task<int> ApplyAsync(TopologyPlan plan, ApplyOptions options, CancellationToken cancellationToken)
    {
        Log.Warning("Spike: apply refused, nothing changes");
        return Task.FromResult(0);
    }

    public Task<int> SetHdrAsync(AttachedDisplay display, bool enabled, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<IReadOnlyList<RefreshRate>> ListRefreshRatesAsync(DisplayIdentity identity, int width, int height, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefreshRate>>([]);
}

/// <summary>Program icons as PNG bytes, like the WinUI app's; <see cref="Views.Converters.IconImage"/> turns them into a bitmap.</summary>
internal static class SpikeIcons
{
    public static object? Extract(string file)
    {
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractIcon(file, 0, 96) ?? System.Drawing.Icon.ExtractAssociatedIcon(file);
            if (icon is null)
            {
                return null;
            }

            using System.Drawing.Bitmap bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or COMException or ExternalException)
        {
            Log.Debug("No icon for {File}: {Reason}", Path.GetFileName(file), ex.Message);
            return null;
        }
    }

    /// <summary>shortcut: AppIcons is internal to Presentation (visible to RigShift.UI only), so the spike sets it by reflection.</summary>
    public static void Install()
    {
        Type? icons = typeof(ProfilesViewModel).Assembly.GetType("RigShift.App.Services.AppIcons");
        icons?.GetProperty("Extract")?.SetValue(null, new Func<string, object?>(Extract));
        if (icons is null)
        {
            Log.Warning("AppIcons not found, program icons stay empty");
        }
    }
}
