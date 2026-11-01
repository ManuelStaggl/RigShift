using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RigShift.App.Services;

/// <summary>
/// Who is at the other end of the command pipe. The name <c>RigShift.&lt;SessionId&gt;</c> is predictable, so another
/// user on the machine could put a pipe of that name up before RigShift does. The server refuses to join such a
/// pipe; the client, which may look for an instance in another session, checks here that what it found was created
/// by this user and lets nobody else in.
/// </summary>
internal static class PipeTrust
{
    /// <summary>
    /// Whether the pipe behind <paramref name="pipe"/> was created by this user and admits this user only – which is
    /// how <see cref="CommandPipeServer"/> creates it. An elevated instance of this user owns its pipe as
    /// <c>BUILTIN\Administrators</c>, which is accepted: an administrator needs no pipe to take the machine over.
    /// </summary>
    public static bool BelongsToThisUser(PipeStream pipe, out string reason)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } user)
        {
            reason = "the current Windows identity has no user SID";
            return false;
        }

        PipeSecurity security;
        try
        {
            security = pipe.GetAccessControl();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            reason = $"its access list could not be read ({ex.Message})";
            return false;
        }

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !(owner.Equals(user) || owner.Equals(administrators)))
        {
            reason = "it was created by somebody else";
            return false;
        }

        foreach (PipeAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(user))
            {
                reason = $"it also lets {rule.IdentityReference.Value} in";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The user at the client end, or <c>null</c> when Windows would not say. Only valid once the client has sent
    /// data – before that, impersonating a pipe client fails by design.
    /// </summary>
    public static SecurityIdentifier? ClientUser(NamedPipeServerStream server)
    {
        ArgumentNullException.ThrowIfNull(server);
        SecurityIdentifier? client = null;
        server.RunAsClient(() =>
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            client = identity.User;
        });
        return client;
    }
}
