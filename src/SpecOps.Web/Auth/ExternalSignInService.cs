using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SpecOps.Core.Security;
using SpecOps.Data;
using SpecOps.Web.Services;

namespace SpecOps.Web.Auth;

public enum SignInOutcome
{
    SignedIn,
    PendingApproval,
    Disabled,
    Failed,
}

public sealed record SignInResult(SignInOutcome Outcome, string? SignInId = null, string? Error = null);

/// <summary>
/// Turns an external login (Entra, Google, OIDC) into a SpecOps user: creates the user on first sign-in,
/// refreshes profile and group memberships every sign-in, applies bootstrap/auto-approval rules, and signs in only active users.
/// </summary>
public sealed partial class ExternalSignInService(
    UserManager<SpecOpsUser> users,
    RoleManager<IdentityRole> roles,
    SignInManager<SpecOpsUser> signIn,
    ISpecOpsAuthorizer authorizer,
    IOptionsMonitor<AuthSettings> settings,
    AuditLog audit,
    ILogger<ExternalSignInService> logger
)
{
    public const string DevUserName = "dev@localhost";

    public async Task<SignInResult> SignInExternalAsync(ExternalLoginInfo info, string? clientIp)
    {
        ArgumentNullException.ThrowIfNull(info);
        var current = settings.CurrentValue;
        current.Providers.TryGetValue(info.LoginProvider, out var provider);
        var identity = ExternalIdentity.Create(info.LoginProvider, info.ProviderKey, info.Principal, provider);

        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user is null)
        {
            user = new SpecOpsUser
            {
                UserName = await UniqueUserNameAsync(identity.Email ?? identity.SignInId, info.LoginProvider),
                Email = identity.Email,
                EmailConfirmed = identity.TrustedEmail is not null,
                DisplayName = identity.DisplayName,
            };

            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                return new SignInResult(
                    SignInOutcome.Failed,
                    identity.SignInId,
                    string.Join(" ", created.Errors.Select(e => e.Description))
                );
            }

            await users.AddLoginAsync(user, info);
            LogUserCreated(user.UserName!, info.LoginProvider);
            await audit.RecordAsync(user.Id, user.Label, AuditActions.UserCreated, identity.SignInId, null, clientIp);
        }

        user.DisplayName = identity.DisplayName ?? user.DisplayName;
        user.Email = identity.Email ?? user.Email;
        user.EmailConfirmed = identity.TrustedEmail is not null;
        user.LastSignInAtUtc = DateTime.UtcNow;
        await SyncGroupsAsync(user, identity.Groups);

        if (identity.IsBootstrapAdmin(current))
        {
            if (await EnsureRoleAsync(user, SpecOpsAuthorizer.AdminRole) || user.Status != UserStatus.Active)
            {
                await audit.RecordAsync(
                    user.Id,
                    user.Label,
                    AuditActions.BootstrapAdmin,
                    identity.SignInId,
                    null,
                    clientIp
                );
            }

            user.Status = UserStatus.Active;
        }
        else if (
            user.Status == UserStatus.Pending
            && (
                identity.MatchesAutoApproveDomain(current)
                || authorizer.MatchesAnyPolicy(await users.GetRolesAsync(user), identity.Groups)
            )
        )
        {
            user.Status = UserStatus.Active;
            LogAutoApproved(user.UserName!);
            await audit.RecordAsync(
                user.Id,
                user.Label,
                AuditActions.UserAutoApproved,
                identity.SignInId,
                null,
                clientIp
            );
        }

        await users.UpdateAsync(user);

        switch (user.Status)
        {
            case UserStatus.Active:
                await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: info.LoginProvider);
                await audit.RecordAsync(user.Id, user.Label, AuditActions.SignIn, identity.SignInId, null, clientIp);
                return new SignInResult(SignInOutcome.SignedIn, identity.SignInId);

            case UserStatus.Disabled:
                await audit.RecordAsync(
                    user.Id,
                    user.Label,
                    AuditActions.SignInDenied,
                    identity.SignInId,
                    null,
                    clientIp
                );
                return new SignInResult(SignInOutcome.Disabled, identity.SignInId);

            default:
                await audit.RecordAsync(
                    user.Id,
                    user.Label,
                    AuditActions.SignInPending,
                    identity.SignInId,
                    null,
                    clientIp
                );
                return new SignInResult(SignInOutcome.PendingApproval, identity.SignInId);
        }
    }

    /// <summary>Development-only shortcut: signs in as an active admin without any identity provider.</summary>
    public async Task SignInDevUserAsync(string? clientIp)
    {
        var user = await users.FindByNameAsync(DevUserName);
        if (user is null)
        {
            user = new SpecOpsUser
            {
                UserName = DevUserName,
                Email = DevUserName,
                EmailConfirmed = true,
                DisplayName = "Dev User",
                Status = UserStatus.Active,
            };
            await users.CreateAsync(user);
        }

        await EnsureRoleAsync(user, SpecOpsAuthorizer.AdminRole);
        user.Status = UserStatus.Active;
        user.LastSignInAtUtc = DateTime.UtcNow;
        await users.UpdateAsync(user);
        await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: "Development");
        await audit.RecordAsync(user.Id, user.Label, AuditActions.SignIn, "Development", null, clientIp);
    }

    private async Task SyncGroupsAsync(SpecOpsUser user, IReadOnlyCollection<string> groups)
    {
        var current = (await users.GetClaimsAsync(user))
            .Where(c => c.Type == SpecOpsAuthorizer.GroupClaimType)
            .ToList();
        var stale = current.Where(c => !groups.Contains(c.Value, StringComparer.OrdinalIgnoreCase)).ToList();
        var added = groups
            .Where(g => !current.Exists(c => string.Equals(c.Value, g, StringComparison.OrdinalIgnoreCase)))
            .Select(g => new Claim(SpecOpsAuthorizer.GroupClaimType, g))
            .ToList();

        if (stale.Count > 0)
        {
            await users.RemoveClaimsAsync(user, stale);
        }

        if (added.Count > 0)
        {
            await users.AddClaimsAsync(user, added);
        }
    }

    /// <summary>Returns true when the role was newly granted.</summary>
    private async Task<bool> EnsureRoleAsync(SpecOpsUser user, string role)
    {
        if (!await roles.RoleExistsAsync(role))
        {
            await roles.CreateAsync(new IdentityRole(role));
        }

        if (await users.IsInRoleAsync(user, role))
        {
            return false;
        }

        await users.AddToRoleAsync(user, role);
        return true;
    }

    private async Task<string> UniqueUserNameAsync(string preferred, string provider)
    {
        var name = preferred;
        for (var i = 2; await users.FindByNameAsync(name) is not null; i++)
        {
            name = $"{preferred} ({provider}{(i > 2 ? $" {i - 1}" : "")})";
        }

        return name;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "New user {UserName} signed in with {Provider}")]
    private partial void LogUserCreated(string userName, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserName} auto-approved")]
    private partial void LogAutoApproved(string userName);
}
