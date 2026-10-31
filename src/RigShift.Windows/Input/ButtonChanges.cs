namespace RigShift.Windows.Input;

/// <summary>
/// Which HID reports change a device's held buttons. The first report per device and report ID only sets the baseline
/// (a latched toggle must not answer for the user); every later difference counts, press or release.
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
