using System.ComponentModel;
using System.Runtime.InteropServices;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Services;

/// <summary>Builds the profile detail's editor, opens the setup assistant and asks the questions around profiles.</summary>
public sealed class ProfileDialogs : IProfilePageDialogs, IHelpPageDialogs
{
    private readonly ProfileEditorServices _editorServices;
    private readonly ProfileCatalog _catalog;
    private readonly IDisplayConfigurator _display;
    private readonly IAudioController _audio;
    private readonly SettingsService _settings;
    private readonly IUsbDeviceList _usbDevices;
    private readonly ISurroundController _surround;
    private readonly IUsbPowerCheck _powerCheck;
    private readonly ActiveProfileMatcher _matcher;
    private readonly DisplayChangeWatcher _displayChanges;
    private readonly SwitchCoordinator _coordinator;
    private readonly ILogger _log;
    private readonly ProfileEditorFactory _factory;

    /// <param name="editorServices">Handed to every editor; its catalog, displays, settings and log serve the dialogs too.</param>
    public ProfileDialogs(
        ProfileEditorServices editorServices,
        IAudioController audio,
        IUsbDeviceList usbDevices,
        ISurroundController surround,
        IUsbPowerCheck powerCheck,
        ActiveProfileMatcher matcher,
        DisplayChangeWatcher displayChanges,
        SwitchCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(editorServices);
        _editorServices = editorServices;
        _catalog = editorServices.Catalog;
        _display = editorServices.Display;
        _settings = editorServices.Settings;
        _audio = audio;
        _usbDevices = usbDevices;
        _surround = surround;
        _powerCheck = powerCheck;
        _matcher = matcher;
        _displayChanges = displayChanges;
        _coordinator = coordinator;
        _log = editorServices.Log.ForContext<ProfileDialogs>();
        _factory = new ProfileEditorFactory(editorServices, audio, usbDevices, surround, powerCheck);
    }

    Task IProfilePageDialogs.ShowSetupAssistantAsync() => ShowSetupAssistantAsync();

    Task IHelpPageDialogs.ShowSetupAssistantAsync() => ShowSetupAssistantAsync();

    Task<bool> IHelpPageDialogs.ConfirmRestoreAsync(BackupContent content) => ConfirmRestoreAsync(content);

    public string? PickZipToSave(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedName,
            DefaultExt = ".zip",
            Filter = "ZIP (*.zip)|*.zip",
            AddExtension = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickZipToOpen()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ZIP (*.zip)|*.zip", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    Task<bool> IProfilePageDialogs.ConfirmDeleteAsync(string name, int ruleCount) => ConfirmDeleteAsync(name, ruleCount);

    Task<UnsavedChoice> IProfilePageDialogs.ConfirmUnsavedAsync(string name, string? targetName) => ConfirmUnsavedAsync(name, targetName);

    public Task<Profile> NewFromCurrentAsync() => _factory.NewFromCurrentAsync();

    public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew) => _factory.CreateEditorAsync(profile, isNew);

    /// <summary>The setup assistant; remembers that it was shown.</summary>
    /// <param name="previewStep">Debug builds: open at this step with demo profiles, for screenshots.</param>
    public async Task ShowSetupAssistantAsync(SetupStep? previewStep = null)
    {
        var viewModel = new SetupWizardViewModel(_catalog, _display, _audio, _usbDevices, _powerCheck, _matcher, _settings, _surround, _editorServices.Hotkeys, _log);
#if DEBUG
        if (previewStep is { } step)
        {
            await viewModel.PreviewAsync(step);
        }
#else
        _ = previewStep;
#endif
        var window = new SetupWizardWindow(viewModel, _displayChanges);

        // Looked up rather than injected: asking the container for it would build the main window just to find it hidden.
        if (System.Windows.Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible) is { } main)
        {
            window.Owner = main;
        }
        else
        {
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        }

        try
        {
            await _settings.UpdateAsync(s => s with { SetupAssistantShown = true }, CancellationToken.None, notify: false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Could not remember that the setup assistant was shown");
        }

        _log.Information("Setup assistant opening at step {Step}", viewModel.Step);
        window.ShowDialog();
        _log.Information("Setup assistant closed");

        // "Try it" on the last step: the first real switch, with the countdown that takes it back.
        if (viewModel.SwitchAfterClose is { } target)
        {
            await _coordinator.SwitchAsync(target);
        }
    }

    /// <summary>Delete is destructive: red text, never the accent.</summary>
    /// <param name="ruleCount">USB rules that go with the profile; named in the question so the loss is no surprise.</param>
    public static async Task<bool> ConfirmDeleteAsync(string name, int ruleCount = 0) =>
        await Ask(
            Loc.Instance["Profile_DeleteTitle"],
            Loc.Format(ruleCount > 0 ? "Profile_DeleteTextWithRules" : "Profile_DeleteText", name),
            Loc.Instance["Profile_Delete"], DialogButtonKind.Danger);

    /// <summary>
    /// Asks whether to undo a switch that never finished, e.g. because RigShift was killed between the apply and the
    /// confirmation. The answer is the user's: their screens may look right by now (Windows restored them, or they
    /// sorted it out by hand), and in that case putting the old layout back would be the disruptive move.
    /// </summary>
    public static async Task<bool> ConfirmRestoreInterruptedAsync(string targetProfileName) =>
        await Ask(
            Loc.Instance["Interrupted_Title"], Loc.Format("Interrupted_Text", targetProfileName),
            Loc.Instance["Interrupted_Restore"], DialogButtonKind.Primary, Loc.Instance["Interrupted_Keep"]);

    /// <summary>
    /// Asks before quitting while a game session runs: without RigShift nothing switches back or closes the tools when the
    /// game ends. An update waits for the exit, so it simply comes later.
    /// </summary>
    public static async Task<bool> ConfirmQuitDuringGameAsync(string games) =>
        await Ask(
            Loc.Instance["QuitGame_Title"], Loc.Format("QuitGame_Text", games),
            Loc.Instance["QuitGame_Quit"], DialogButtonKind.Danger, Loc.Instance["QuitGame_Stay"]);

    /// <summary>
    /// Told once at startup: the settings file was unusable and defaults are in force. Not a toast – what is gone
    /// (rules, hotkeys, the update choice) changes how the machine behaves, and the copy is only useful if the user
    /// knows it exists.
    /// </summary>
    /// <returns><c>true</c> when the user wants to see the folder with the copy.</returns>
    public static async Task<bool> ShowSettingsProblemAsync(Core.Settings.SettingsLoadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string? copy = report.BackupFile is null ? null : System.IO.Path.GetFileName(report.BackupFile);
        bool newer = report.Problem == Core.Settings.SettingsLoadProblem.FromNewerVersion;
        string text = newer ? Loc.Format("SettingsProblem_NewerText", copy ?? "–")
            : copy is null ? Loc.Instance["SettingsProblem_TextNoCopy"]
            : Loc.Format("SettingsProblem_TextCopy", copy);
        return await Ask(
            Loc.Instance[newer ? "SettingsProblem_NewerTitle" : "SettingsProblem_Title"], text,
            Loc.Instance["SettingsProblem_OpenFolder"], DialogButtonKind.Secondary, Loc.Instance["Common_Close"]);
    }

    /// <summary>Asked when the same UI exception keeps coming back; restarting is the primary way out.</summary>
    public static async Task<RepeatedErrorChoice> AskAboutRepeatedErrorAsync(string message) =>
        (RepeatedErrorChoice)await DialogWindow.AskAsync(
            Loc.Instance["Crash_Title"], Loc.Format("Crash_Text", message),
            [
                new DialogChoice(Loc.Instance["Crash_Restart"], DialogButtonKind.Primary, (int)RepeatedErrorChoice.Restart),
                new DialogChoice(Loc.Instance["Crash_OpenLog"], DialogButtonKind.Secondary, (int)RepeatedErrorChoice.OpenLog),
                new DialogChoice(Loc.Instance["Crash_Continue"], DialogButtonKind.Secondary, (int)RepeatedErrorChoice.Continue),
            ],
            cancelResult: (int)RepeatedErrorChoice.Continue);

    /// <summary>Same style as deleting a profile.</summary>
    /// <param name="deviceName">The rule's USB device, or <c>null</c> when none is chosen.</param>
    public static async Task<bool> ConfirmDeleteRuleAsync(string? deviceName) =>
        await Ask(
            Loc.Instance["Automation_DeleteTitle"],
            deviceName is null ? Loc.Instance["Automation_DeleteTextNoDevice"] : Loc.Format("Automation_DeleteText", deviceName),
            Loc.Instance["Profile_Delete"], DialogButtonKind.Danger);

    /// <summary>
    /// Restoring a backup replaces everything: same style as deleting (1.7.0), with everything the backup would start,
    /// end, switch or bind listed first (<see cref="RestoreQuestion"/>).
    /// </summary>
    public static async Task<bool> ConfirmRestoreAsync(BackupContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return await DialogWindow.AskAsync(
            Loc.Instance["About_RestoreTitle"], RestoreQuestion.Message(content),
            [
                new DialogChoice(Loc.Instance["About_Restore"], DialogButtonKind.Danger, 1),
                new DialogChoice(Loc.Instance["Common_Cancel"], DialogButtonKind.Secondary, 0),
            ],
            cancelResult: 0,
            [.. RestoreQuestion.Lines(content).Select(l => new DialogDetail(l.Heading, l.Text, l.Warning))]) == 1;
    }

    /// <summary>
    /// Asked when the selection or the navigation leaves a profile with unsaved changes: "Save changes to X?"
    /// with Save, Discard and Cancel. Save is the primary button: the changes were made on purpose.
    /// </summary>
    /// <param name="targetName">What the user is switching to, when that is known; it explains why the dialog is here.</param>
    public static async Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName = null)
    {
        int answer = await DialogWindow.AskAsync(
            Loc.Format("Unsaved_Title", name),
            targetName is null ? Loc.Instance["Unsaved_Text"] : Loc.Format("Unsaved_TextSwitch", targetName),
            [
                new DialogChoice(Loc.Instance["Common_Save"].Replace("_", string.Empty, StringComparison.Ordinal), DialogButtonKind.Primary, 1),
                new DialogChoice(Loc.Instance["Common_Discard"], DialogButtonKind.Secondary, 2),
                new DialogChoice(Loc.Instance["Common_Cancel"], DialogButtonKind.Secondary, 0),
            ],
            cancelResult: 0);
        return answer switch
        {
            1 => UnsavedChoice.Save,
            2 => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    /// <summary>A yes/no question: the action first, the way out second (Windows order).</summary>
    private static async Task<bool> Ask(string title, string message, string actionText, DialogButtonKind kind, string? cancelText = null) =>
        await DialogWindow.AskAsync(
            title, message,
            [
                new DialogChoice(actionText, kind, 1),
                new DialogChoice(cancelText ?? Loc.Instance["Common_Cancel"], DialogButtonKind.Secondary, 0),
            ],
            cancelResult: 0) == 1;
}
