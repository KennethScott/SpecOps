using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using SpecOps.Core.Security;
using SpecOps.Data;
using SpecOps.Web.Auth;

namespace SpecOps.Web.Services;

/// <summary>
/// Deleting users (people who've left). Removes the account with its roles, group claims and sign-in links; their
/// names stay in run history and the audit log. Open sessions end within a minute (the security stamp check finds
/// no user). Signing in again creates a new account that goes through approval like anyone new.
/// </summary>
public sealed class UserAdmin(IServiceScopeFactory scopes, ISpecOpsAuthorizer authorizer, AuditLog audit)
{
    /// <summary>Returns an error message, or null when the user was deleted.</summary>
    public async Task<string?> DeleteAsync(ClaimsPrincipal actor, string userId, string? clientIp = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!authorizer.GetAccess(actor).IsAdmin)
        {
            return "Only administrators can delete users.";
        }

        if (string.Equals(userId, actor.UserId(), StringComparison.Ordinal))
        {
            return "You can't delete your own account.";
        }

        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SpecOpsUser>>();
        if (await users.FindByIdAsync(userId) is not { } user)
        {
            return "That user no longer exists.";
        }

        var logins = await users.GetLoginsAsync(user);
        var roles = await users.GetRolesAsync(user);
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return $"Couldn't delete {user.Label}: {string.Join(" ", result.Errors.Select(e => e.Description))}";
        }

        await audit.RecordAsync(
            actor,
            AuditActions.UserDeleted,
            user.Label,
            $"{user.Email}; was {user.Status}; roles: {string.Join(", ", roles)}; sign-ins: {string.Join(", ", logins.Select(l => $"{l.LoginProvider}:{l.ProviderKey}"))}",
            clientIp
        );
        return null;
    }
}
