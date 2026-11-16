using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SpecOps.Core.Security;
using SpecOps.Data;

namespace SpecOps.Web.Auth;

public enum SignInOutcome
{
    SignedIn,
    PendingApproval,
    Disabled,
    Failed,
}

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
    ILogger<ExternalSignInService> logger)
{
    public const string DevUserName = "dev@localhost";

    public async Task<(SignInOutcome Outcome, string? Error)> SignInExternalAsync(ExternalLoginInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var principal = info.Principal;
        var email = FindEmail(principal);
        var emailVerified = !string.Equals(principal.FindFirstValue("email_verified"), "false", StringComparison.OrdinalIgnoreCase);
        var displayName = principal.FindFirstValue("name") ?? principal.FindFirstValue(ClaimTypes.Name) ?? email;
        var groupsClaim = settings.CurrentValue.Providers.TryGetValue(info.LoginProvider, out var provider) ? provider.GroupsClaim : "groups";
        var groups = principal.FindAll(groupsClaim).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user is null)
        {
            user = new SpecOpsUser
            {
                UserName = await UniqueUserNameAsync(email ?? $"{info.LoginProvider}:{info.ProviderKey}", info.LoginProvider),
                Email = email,
                EmailConfirmed = email is not null && emailVerified,
                DisplayName = displayName,
            };

            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                return (SignInOutcome.Failed, string.Join(" ", created.Errors.Select(e => e.Description)));
            }

            await users.AddLoginAsync(user, info);
            LogUserCreated(user.UserName!, info.LoginProvider);
        }

        user.DisplayName = displayName ?? user.DisplayName;
        user.Email = email ?? user.Email;
        user.LastSignInAtUtc = DateTime.UtcNow;
        await SyncGroupsAsync(user, groups);

        var trustedEmail = emailVerified ? email : null;
        if (IsBootstrapAdmin(trustedEmail))
        {
            await EnsureRoleAsync(user, SpecOpsAuthorizer.AdminRole);
            user.Status = UserStatus.Active;
        }
        else if (user.Status == UserStatus.Pending && ShouldAutoApprove(trustedEmail, groups, await users.GetRolesAsync(user)))
        {
            user.Status = UserStatus.Active;
            LogAutoApproved(user.UserName!);
        }

        await users.UpdateAsync(user);

        return user.Status switch
        {
            UserStatus.Active => await CompleteSignInAsync(user, info.LoginProvider),
            UserStatus.Disabled => (SignInOutcome.Disabled, null),
            _ => (SignInOutcome.PendingApproval, null),
        };
    }

    /// <summary>Development-only shortcut: signs in as an active admin without any identity provider.</summary>
    public async Task SignInDevUserAsync()
    {
        var user = await users.FindByNameAsync(DevUserName);
        if (user is null)
        {
            user = new SpecOpsUser { UserName = DevUserName, Email = DevUserName, EmailConfirmed = true, DisplayName = "Dev User", Status = UserStatus.Active };
            await users.CreateAsync(user);
        }

        await EnsureRoleAsync(user, SpecOpsAuthorizer.AdminRole);
        user.Status = UserStatus.Active;
        user.LastSignInAtUtc = DateTime.UtcNow;
        await users.UpdateAsync(user);
        await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: "Development");
    }

    private async Task<(SignInOutcome, string?)> CompleteSignInAsync(SpecOpsUser user, string provider)
    {
        await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: provider);
        return (SignInOutcome.SignedIn, null);
    }

    private async Task SyncGroupsAsync(SpecOpsUser user, IReadOnlyCollection<string> groups)
    {
        var current = (await users.GetClaimsAsync(user)).Where(c => c.Type == SpecOpsAuthorizer.GroupClaimType).ToList();
        var stale = current.Where(c => !groups.Contains(c.Value, StringComparer.OrdinalIgnoreCase)).ToList();
        var added = groups.Where(g => !current.Exists(c => string.Equals(c.Value, g, StringComparison.OrdinalIgnoreCase)))
            .Select(g => new Claim(SpecOpsAuthorizer.GroupClaimType, g)).ToList();

        if (stale.Count > 0)
        {
            await users.RemoveClaimsAsync(user, stale);
        }

        if (added.Count > 0)
        {
            await users.AddClaimsAsync(user, added);
        }
    }

    private async Task EnsureRoleAsync(SpecOpsUser user, string role)
    {
        if (!await roles.RoleExistsAsync(role))
        {
            await roles.CreateAsync(new IdentityRole(role));
        }

        if (!await users.IsInRoleAsync(user, role))
        {
            await users.AddToRoleAsync(user, role);
        }
    }

    private bool IsBootstrapAdmin(string? email) =>
        email is not null && settings.CurrentValue.BootstrapAdmins.Contains(email, StringComparer.OrdinalIgnoreCase);

    private bool ShouldAutoApprove(string? email, IEnumerable<string> groups, IEnumerable<string> userRoles)
    {
        var domain = email?.Split('@') is [_, var d] ? d : null;
        return (domain is not null && settings.CurrentValue.AutoApproveEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            || authorizer.MatchesAnyPolicy(userRoles, groups);
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

    private static string? FindEmail(ClaimsPrincipal principal)
    {
        var email = principal.FindFirstValue("email") ?? principal.FindFirstValue(ClaimTypes.Email);
        if (email is null && principal.FindFirstValue("preferred_username") is { } upn && upn.Contains('@', StringComparison.Ordinal))
        {
            email = upn;
        }

        return email;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "New user {UserName} signed in with {Provider}")]
    private partial void LogUserCreated(string userName, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserName} auto-approved")]
    private partial void LogAutoApproved(string userName);
}
