namespace RigShift.App.Services;

/// <summary>The countdown after a switch, confirmed by pressing the profile's hotkey again.</summary>
public interface IOpenConfirmation
{
    /// <returns><c>true</c> when a countdown for <paramref name="profileId"/> was open and is now confirmed.</returns>
    bool TryConfirm(Guid profileId);
}
