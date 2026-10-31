using System.Drawing;
using System.Runtime.InteropServices;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;
using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace RigShift.Windows.Shell;

/// <summary>
/// Reads and restores the positions of the desktop symbols through the shell's own view of the desktop.
///
/// Not through the desktop's SysListView32: since Windows 10 1809 the shell keeps the icon layout itself, and
/// positions poked into that control are ignored or overwritten again. <c>IFolderView</c> is the documented way and
/// the one Microsoft points at, see docs/desktop-icons.md.
/// </summary>
public sealed class DesktopIcons : IDesktopIcons
{
    // A full desktop is read and placed in well under a second; this is for an Explorer that hangs.
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);

    private readonly ILogger _log;

    public DesktopIcons(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public DesktopIconLayout? Capture() => TryOnShellThread(
        static (view, _) => new DesktopIconLayout { Icons = ReadPositions(view), CapturedAt = DateTimeOffset.Now },
        "read the desktop symbols",
        out DesktopIconLayout layout)
        ? layout
        : null;

    public DesktopIconResult Restore(DesktopIconLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsEmpty)
        {
            return DesktopIconResult.NotConfigured;
        }

        return TryOnShellThread((view, log) => Place(view, layout, log), "put the desktop symbols back", out DesktopIconResult result)
            ? result
            : DesktopIconResult.Unavailable;
    }

    // Gone symbols are skipped and new ones keep their place: a profile's layout is a wish, not a demand.
    private static DesktopIconResult Place(IFolderView view, DesktopIconLayout layout, ILogger log)
    {
        if (AutoArranges(view, log))
        {
            return new DesktopIconResult(DesktopIconOutcome.AutoArrange, 0, 0);
        }

        Dictionary<string, int> wanted = [];
        for (int i = 0; i < layout.Icons.Count; i++)
        {
            wanted[layout.Icons[i].Item] = i;
        }

        var pidls = new List<nint>();
        var points = new List<Point>();
        List<(string Name, nint Pidl)> items = Enumerate(view);
        try
        {
            foreach ((string name, nint pidl) in items)
            {
                if (wanted.Remove(name, out int index))
                {
                    pidls.Add(pidl);
                    points.Add(new Point(layout.Icons[index].X, layout.Icons[index].Y));
                }
            }

            if (pidls.Count > 0)
            {
                PositionItems(view, pidls, points);
            }

            return new DesktopIconResult(DesktopIconOutcome.Restored, pidls.Count, wanted.Count);
        }
        finally
        {
            Free(items);
        }
    }

    private static unsafe void PositionItems(IFolderView view, List<nint> pidls, List<Point> points)
    {
        var handles = new nint[pidls.Count];
        pidls.CopyTo(handles);
        Point[] targets = [.. points];

        fixed (nint* apidl = handles)
        fixed (Point* apt = targets)
        {
            // SVSI_POSITIONITEM alone: position the items without changing what is selected (shobjidl_core.h, 0x80).
            view.SelectAndPositionItems((uint)pidls.Count, (ITEMIDLIST**)apidl, apt, SvsiPositionItem);
        }
    }

    private static List<DesktopIcon> ReadPositions(IFolderView view)
    {
        List<(string Name, nint Pidl)> items = Enumerate(view);
        try
        {
            var icons = new List<DesktopIcon>(items.Count);
            foreach ((string name, nint pidl) in items)
            {
                Point at = PositionOf(view, pidl);
                icons.Add(new DesktopIcon { Item = name, X = at.X, Y = at.Y });
            }

            return icons;
        }
        finally
        {
            Free(items);
        }
    }

    private static unsafe Point PositionOf(IFolderView view, nint pidl)
    {
        Point point;
        view.GetItemPosition((ITEMIDLIST*)pidl, &point);
        return point;
    }

    // The caller owns the PIDLs and frees them; reading them all at once keeps that ownership in one place.
    private static unsafe List<(string Name, nint Pidl)> Enumerate(IFolderView view)
    {
        var items = new List<(string Name, nint Pidl)>();
        PInvoke.SHGetDesktopFolder(out IShellFolder desktop).ThrowOnFailure();
        view.ItemCount(_SVGIO.SVGIO_ALLVIEW, out int count);
        for (int i = 0; i < count; i++)
        {
            ITEMIDLIST* pidl;
            view.Item(i, &pidl);
            if (pidl is null)
            {
                continue;
            }

            string? name = null;
            try
            {
                STRRET text;
                desktop.GetDisplayNameOf(pidl, SHGDNF.SHGDN_FORPARSING, &text);
                name = NameOf(&text, pidl);
            }
            catch (COMException)
            {
                // An item that vanished between the count and the name: skip it, the rest is still worth keeping.
            }

            if (name is null)
            {
                PInvoke.ILFree(pidl);
                continue;
            }

            items.Add((name, (nint)pidl));
        }

        return items;
    }

    // In whichever of its three forms the folder chose to answer.
    private static unsafe string? NameOf(STRRET* text, ITEMIDLIST* pidl)
    {
        switch (text->uType)
        {
            case StrRetWStr:
                string? wide = text->pOleStr.ToString();
                Marshal.FreeCoTaskMem((nint)text->pOleStr.Value);
                return wide;
            case StrRetOffset:
                return Marshal.PtrToStringAnsi((nint)((byte*)pidl + text->uOffset));
            case StrRetCStr:
                return Marshal.PtrToStringAnsi((nint)(&text->Anonymous.cStr));
            default:
                return null;
        }
    }

    // Auto-arrange overrides every position we set; "Align icons to grid" only snaps them, which is fine.
    private static bool AutoArranges(IFolderView view, ILogger log)
    {
        if (view is not IFolderView2 flags)
        {
            return false;
        }

        try
        {
            flags.GetCurrentFolderFlags(out uint current);
            bool auto = (current & (uint)FOLDERFLAGS.FWF_AUTOARRANGE) != 0;
            if (auto)
            {
                log.Warning("Desktop symbols not restored: Windows arranges them itself (\"Auto arrange icons\")");
            }

            return auto;
        }
        catch (COMException ex)
        {
            log.Debug(ex, "The desktop view did not report its folder flags");
            return false;
        }
    }

    // The shell hands its view to an apartment thread and switches run on background threads, so this brings its own.
    private bool TryOnShellThread<T>(Func<IFolderView, ILogger, T> work, string what, out T result)
    {
        (bool finished, object? value, Exception? failure) = StaCall.Run<object>(
            () => OpenDesktopView() is { } view ? work(view, _log) : null,
            TimeLimit);

        if (!finished)
        {
            _log.Warning("Could not {What}: Explorer did not answer within {Limit}", what, TimeLimit);
        }
        else if (failure is not null)
        {
            _log.Warning(failure, "Could not {What}", what);
        }
        else if (value is null)
        {
            _log.Warning("Could not {What}: this session has no desktop view", what);
        }

        result = value is null ? default! : (T)value;
        return value is not null;
    }

    // Shell window list → service provider → browser → active view; null without a desktop (service, SSH) or while
    // Explorer restarts.
    private static IFolderView? OpenDesktopView()
    {
        var windows = (IShellWindows)new ShellWindows();
        object desktop = 0;  // CSIDL_DESKTOP
        object root = 0;
        object? dispatch = windows.FindWindowSW(
            in desktop, in root, ShellWindowTypeConstants.SWC_DESKTOP, out _, ShellWindowFindWindowOptions.SWFO_NEEDDISPATCH);
        if (dispatch is not IServiceProvider provider)
        {
            return null;
        }

        Guid service = SidSTopLevelBrowser;
        Guid wanted = typeof(IShellBrowser).GUID;
        if (provider.QueryService(in service, in wanted, out object browser) != 0)
        {
            return null;
        }

        ((IShellBrowser)browser).QueryActiveShellView(out object shellView);
        return shellView as IFolderView;
    }

    private static unsafe void Free(List<(string Name, nint Pidl)> items)
    {
        foreach ((_, nint pidl) in items)
        {
            PInvoke.ILFree((ITEMIDLIST*)pidl);
        }
    }

    // SVSI_POSITIONITEM (shobjidl_core.h): position the item without touching the selection.
    private const uint SvsiPositionItem = 0x80;

    private const uint StrRetWStr = 0;
    private const uint StrRetOffset = 1;
    private const uint StrRetCStr = 2;

    // SID_STopLevelBrowser, the service that hands out the view's browser.
    private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
}

/// <summary>
/// Hand-written because CsWin32 refuses it for AnyCPU. Only <see cref="QueryActiveShellView"/> is ever called; the
/// members before it are placeholders that keep the vtable order, so they must not be removed or reordered.
/// </summary>
[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    void GetWindow(out nint phwnd);

    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

    void InsertMenusSB(nint hmenuShared, nint lpMenuWidths);

    void SetMenuSB(nint hmenuShared, nint holemenuRes, nint hwndActiveObject);

    void RemoveMenusSB(nint hmenuShared);

    void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);

    void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);

    void TranslateAcceleratorSB(nint pmsg, ushort wID);

    void BrowseObject(nint pidl, uint wFlags);

    void GetViewStateStream(uint grfMode, out nint ppStrm);

    void GetControlWindow(uint id, out nint phwnd);

    void SendControlMsg(uint id, uint uMsg, nint wParam, nint lParam, nint pret);

    void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);

    void OnViewWindowActive([MarshalAs(UnmanagedType.IUnknown)] object pshv);

    void SetToolbarItems(nint lpButtons, uint nButtons, uint uFlags);
}

/// <summary>COM's IServiceProvider, hand-written for the same reason as <see cref="IShellBrowser"/>.</summary>
[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig]
    int QueryService(in Guid guidService, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
}
