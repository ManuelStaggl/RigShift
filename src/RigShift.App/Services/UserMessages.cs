using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using RigShift.App.Localization;

namespace RigShift.App.Services;

/// <summary>
/// Turns an exception or a Windows error code into a sentence the user can act on. .NET's own texts are English, name
/// full paths and say what failed rather than what to do; they stay in the log, which every caller writes first.
/// </summary>
public static class UserMessages
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorDiskFull = 112;
    private const int ErrorElevationRequired = 740;
    private const int ErrorCancelled = 1223;
    private const int ErrorBadConfiguration = 1610;
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            Win32Exception win32 => DescribeWindowsError(win32.NativeErrorCode),
            UnauthorizedAccessException => Loc.Instance["Error_NoAccess"],
            FileNotFoundException or DirectoryNotFoundException => Loc.Instance["Error_NotFound"],
            InvalidDataException or JsonException => Loc.Instance["Error_Damaged"],
            COMException { HResult: ClipboardCantOpen } => Loc.Instance["Error_ClipboardBusy"],
            IOException io => (io.HResult & 0xFFFF) switch
            {
                ErrorSharingViolation or ErrorLockViolation => Loc.Instance["Error_FileInUse"],
                ErrorDiskFull => Loc.Instance["Error_DiskFull"],
                _ => Unexpected(exception),
            },
            _ => Unexpected(exception),
        };
    }

    /// <summary>A failed display change, from the code Windows returned.</summary>
    public static string DescribeSwitchError(int code) => code switch
    {
        ErrorAccessDenied => Loc.Instance["Error_Switch5"],
        ErrorInvalidParameter => Loc.Instance["Error_Switch87"],
        ErrorNotSupported => Loc.Instance["Error_Switch50"],
        ErrorBadConfiguration => Loc.Instance["Error_Switch1610"],
        _ => Loc.Format("Error_WindowsCode", code),
    };

    /// <summary>Anything else that failed with a Windows code, such as starting a program.</summary>
    private static string DescribeWindowsError(int code) => code switch
    {
        ErrorFileNotFound or ErrorPathNotFound => Loc.Instance["Error_NotFound"],
        ErrorAccessDenied => Loc.Instance["Error_NoAccess"],
        ErrorSharingViolation or ErrorLockViolation => Loc.Instance["Error_FileInUse"],
        ErrorElevationRequired => Loc.Instance["Error_NeedsAdmin"],
        ErrorCancelled => Loc.Instance["Error_Cancelled"],
        _ => Loc.Format("Error_WindowsCode", code),
    };

    private static string Unexpected(Exception exception) => Loc.Format("Error_Unexpected", exception.GetType().Name);
}
