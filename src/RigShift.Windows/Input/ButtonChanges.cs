namespace RigShift.Windows.Input;

/// <summary>
/// Which HID reports change the buttons a device holds. The first report of each device and report ID only sets the
/// baseline: it shows what was held before anyone listened – a latched toggle on a button box, a stuck paddle – and
/// must not answer for the user. Every later difference counts, a press as much as a release: a device that reports
/// only on change sends the press as its first report, so its release is the first change that can be seen.
/// </summary>
internal sealed class ButtonChanges
{
    private readonly Dictionary<(nint Device, byte ReportId), uint[]> _held = [];

    /// <summary>
    /// Records the buttons one report holds; <paramref name="pressed"/> is usage page and usage per button, in any
    /// order (it is sorted in place). True when they differ from the last report of the same device and report ID.
    /// </summary>
    public bool Update(nint device, byte reportId, Span<uint> pressed)
    {
        pressed.Sort();
        var key = (device, reportId);
        if (_held.TryGetValue(key, out uint[]? before) && pressed.SequenceEqual(before))
        {
            return false;
        }

        _held[key] = pressed.ToArray();
        return before is not null;
    }
}
