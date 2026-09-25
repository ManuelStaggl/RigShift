using System.Text.Json;
using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.Core.Storage;

/// <summary>
/// Compares profiles and games by what the store writes for them. Record equality compares lists by reference, and a
/// hand-written comparison misses the next new field; the stored form cannot. Used to tell whether an entry changed on
/// disk while an editor had it open.
/// </summary>
public static class StoredForm
{
    public static bool Same(Profile? a, Profile? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && Of(a) == Of(b));

    public static bool Same(GameEntry? a, GameEntry? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && Of(a) == Of(b));

    private static string Of(Profile profile) =>
        JsonSerializer.Serialize(new ProfileDocument(JsonProfileStore.CurrentSchemaVersion, profile), ProfileJsonContext.Default.ProfileDocument);

    private static string Of(GameEntry game) =>
        JsonSerializer.Serialize(new GameDocument(JsonGameStore.CurrentSchemaVersion, [game]), GameJsonContext.Default.GameDocument);
}
