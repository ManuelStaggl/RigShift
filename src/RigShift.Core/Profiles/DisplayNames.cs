namespace RigShift.Core.Profiles;

/// <summary>Human-readable display names for logs and messages.</summary>
public static class DisplayNames
{
    public static string Of(DisplayIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return string.IsNullOrWhiteSpace(identity.FriendlyName) ? identity.TargetDevicePath : identity.FriendlyName;
    }
}
