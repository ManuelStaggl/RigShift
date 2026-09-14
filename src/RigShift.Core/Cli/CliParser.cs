using System.CommandLine;
using System.Globalization;

namespace RigShift.Core.Cli;

public enum CliCommand
{
    /// <summary>No command: start (or show) the tray app.</summary>
    None,
    Apply,
    List,
    Save,
    Status,
}

/// <summary>What <c>RigShift.exe</c> was asked to do.</summary>
public sealed record CliRequest
{
    public CliCommand Command { get; init; }

    public string? ProfileName { get; init; }

    public bool NoConfirm { get; init; }

    public bool DryRun { get; init; }

    /// <summary>
    /// Set by <see cref="RigShiftUri"/> through the hidden <c>--from-link</c> option, so it survives the pipe to the
    /// running app: the switch always asks for confirmation.
    /// </summary>
    public bool FromLink { get; init; }

    /// <summary>Start in the tray without opening the main window (autostart, CLI launch).</summary>
    public bool Minimized { get; init; }

    /// <summary>Debug builds only: show the confirmation window without switching.</summary>
    public bool PreviewConfirmation { get; init; }

    /// <summary>Debug builds only: write a tray icon sheet into this folder and show the tray popup in a window.</summary>
    public string? PreviewBranding { get; init; }

    /// <summary>Debug builds only: <c>light</c> or <c>dark</c> instead of the Windows theme, for screenshots.</summary>
    public string? PreviewTheme { get; init; }
}

/// <summary>Either a request to run, or text to print (help, version, usage errors) with its exit code.</summary>
public sealed record CliParseResult(CliRequest? Request, int ExitCode, string Output);

/// <summary>
/// Command line of <c>RigShift.exe</c> (docs/PLAN.md, section 4.4):
/// <c>apply &lt;name&gt; [--no-confirm] [--dry-run]</c>, <c>list</c>, <c>save &lt;name&gt;</c>, <c>status</c>.
/// </summary>
public static class CliParser
{
    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // System.CommandLine localizes its own texts (option help, errors) when symbols are created and parsed;
        // keep everything in English like our own texts.
        CultureInfo culture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            return ParseInvariant(args);
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
        }
    }

    private static CliParseResult ParseInvariant(IReadOnlyList<string> args)
    {
        var minimized = new Option<bool>("--minimized") { Description = "Start in the tray without opening the window." };
        var preview = new Option<bool>("--preview-confirmation") { Hidden = true };
        var previewBranding = new Option<string>("--preview-branding") { Hidden = true };
        var previewTheme = new Option<string>("--preview-theme") { Hidden = true };

        var applyName = new Argument<string>("name") { Description = "Profile name (not case-sensitive)." };
        var noConfirm = new Option<bool>("--no-confirm") { Description = "Keep the new arrangement without asking." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Check the profile against the connected displays without switching." };
        var fromLink = new Option<bool>(RigShiftUri.FromLinkOption) { Hidden = true };
        var apply = new Command("apply", "Switch to a profile.") { applyName, noConfirm, dryRun, fromLink };

        var saveName = new Argument<string>("name") { Description = "Profile name. An existing profile with this name is updated." };
        var save = new Command("save", "Save the current display arrangement and default audio device as a profile.") { saveName };

        var list = new Command("list", "List all profiles; the active one is marked with *.");
        var status = new Command("status", "Show the active profile and the active displays.");

        var root = new RootCommand("RigShift switches displays and audio between profiles.") { minimized, preview, previewBranding, previewTheme, apply, list, save, status };

        // Every command gets a no-op action. A parse result whose action differs is help, version or an error.
        foreach (Command command in (Command[])[root, apply, list, save, status])
        {
            command.SetAction(_ => CliExitCodes.Applied);
        }

        ParseResult parsed = root.Parse(args);
        if (parsed.Errors.Count > 0 || parsed.Action != parsed.CommandResult.Command.Action)
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            int exitCode = parsed.Invoke(new InvocationConfiguration { Output = output, Error = output });
            return new CliParseResult(null, parsed.Errors.Count > 0 ? CliExitCodes.InvalidArguments : exitCode, output.ToString().Trim());
        }

        Command chosen = parsed.CommandResult.Command;
        var request = new CliRequest
        {
            Minimized = parsed.GetValue(minimized),
            PreviewConfirmation = parsed.GetValue(preview),
            PreviewBranding = parsed.GetValue(previewBranding),
            PreviewTheme = parsed.GetValue(previewTheme),
        };

        request = chosen == apply ? request with
            {
                Command = CliCommand.Apply,
                ProfileName = parsed.GetValue(applyName),
                NoConfirm = parsed.GetValue(noConfirm),
                DryRun = parsed.GetValue(dryRun),
                FromLink = parsed.GetValue(fromLink),
            }
            : chosen == save ? request with { Command = CliCommand.Save, ProfileName = parsed.GetValue(saveName) }
            : chosen == list ? request with { Command = CliCommand.List }
            : chosen == status ? request with { Command = CliCommand.Status }
            : request;

        return new CliParseResult(request, CliExitCodes.Applied, string.Empty);
    }
}
