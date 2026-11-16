using System.Collections.Frozen;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;
using SpecOps.Core.Scripts;

namespace SpecOps.Core.Security;

/// <summary>
/// Grants script categories (and optionally admin rights) to app roles and/or external groups.
/// </summary>
public sealed class PolicyDefinition
{
    public string Name { get; set; } = "";

    /// <summary>Members can use the Terminal and the Admin pages.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>App roles (assigned to users in the Admin UI).</summary>
    public IList<string> Roles { get; init; } = [];

    /// <summary>External group values from the sign-in provider (Entra group object IDs, OIDC "groups" claim values, DOMAIN\group).</summary>
    public IList<string> Groups { get; init; } = [];

    /// <summary>Category IDs this policy can run. "*" means every category.</summary>
    public IList<string> Categories { get; init; } = [];
}

/// <summary>What a user may do, computed from their roles and groups.</summary>
public sealed record UserAccess(bool IsAdmin, bool AllCategories, IReadOnlySet<string> Categories, IReadOnlyList<string> Policies)
{
    public static UserAccess None { get; } = new(false, false, FrozenSet<string>.Empty, []);

    public bool HasAnyAccess => IsAdmin || AllCategories || Categories.Count > 0;

    public bool CanAccessCategory(string categoryId) => AllCategories || Categories.Contains(categoryId);

    public bool CanRun(ScriptDefinition script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return CanAccessCategory(script.CategoryId);
    }
}

public interface ISpecOpsAuthorizer
{
    UserAccess GetAccess(ClaimsPrincipal user);

    /// <summary>True if any policy grants access to someone with these roles/groups (used for auto-approval).</summary>
    bool MatchesAnyPolicy(IEnumerable<string> roles, IEnumerable<string> groups);
}

public sealed class SpecOpsAuthorizer(IOptionsMonitor<SpecOpsOptions> options) : ISpecOpsAuthorizer
{
    /// <summary>Claim type used for external group memberships stored on the user.</summary>
    public const string GroupClaimType = "specops:group";

    public const string AllCategoriesWildcard = "*";

    /// <summary>Built-in app role: full admin and every category, regardless of configured policies.</summary>
    public const string AdminRole = "Admin";

    private static readonly UserAccess BuiltInAdmin = new(true, true, FrozenSet<string>.Empty, ["Admin (built-in role)"]);

    public UserAccess GetAccess(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            return UserAccess.None;
        }

        if (user.IsInRole(AdminRole))
        {
            return BuiltInAdmin;
        }

        var groups = user.FindAll(GroupClaimType).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Evaluate(policy => policy.Roles.Any(user.IsInRole) || policy.Groups.Any(groups.Contains));
    }

    public bool MatchesAnyPolicy(IEnumerable<string> roles, IEnumerable<string> groups)
    {
        var roleSet = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupSet = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Evaluate(p => p.Roles.Any(roleSet.Contains) || p.Groups.Any(groupSet.Contains)).HasAnyAccess;
    }

    private UserAccess Evaluate(Func<PolicyDefinition, bool> isMember)
    {
        var isAdmin = false;
        var all = false;
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<string>();

        foreach (var policy in options.CurrentValue.Policies)
        {
            if (!isMember(policy))
            {
                continue;
            }

            matched.Add(policy.Name);
            isAdmin |= policy.IsAdmin;
            foreach (var category in policy.Categories)
            {
                if (category == AllCategoriesWildcard)
                {
                    all = true;
                }
                else
                {
                    categories.Add(category);
                }
            }
        }

        return matched.Count == 0
            ? UserAccess.None
            : new UserAccess(isAdmin, all, categories.ToFrozenSet(StringComparer.OrdinalIgnoreCase), matched);
    }
}
