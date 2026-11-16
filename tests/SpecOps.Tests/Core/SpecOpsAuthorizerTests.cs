using System.Security.Claims;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;

namespace SpecOps.Tests.Core;

public class SpecOpsAuthorizerTests
{
    private static readonly SpecOpsAuthorizer Authorizer = new(
        TestOptions.Create(o =>
        {
            o.Policies.Add(
                new PolicyDefinition
                {
                    Name = "Admins",
                    IsAdmin = true,
                    Roles = ["Admin"],
                    Categories = ["Admin"],
                }
            );
            o.Policies.Add(
                new PolicyDefinition
                {
                    Name = "HelpDesk",
                    Roles = ["HelpDesk"],
                    Groups = ["helpdesk-group-id"],
                    Categories = ["Users", "Testing"],
                }
            );
            o.Policies.Add(
                new PolicyDefinition
                {
                    Name = "Everything",
                    Roles = ["SuperUser"],
                    Categories = ["*"],
                }
            );
        })
    );

    private static ClaimsPrincipal User(string[]? roles = null, string[]? groups = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "someone") };
        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((groups ?? []).Select(g => new Claim(SpecOpsAuthorizer.GroupClaimType, g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Anonymous_user_has_no_access()
    {
        var access = Authorizer.GetAccess(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.False(access.HasAnyAccess);
    }

    [Fact]
    public void Authenticated_user_without_matching_policy_has_no_access()
    {
        Assert.False(Authorizer.GetAccess(User(roles: ["Nobody"])).HasAnyAccess);
    }

    [Fact]
    public void Role_grants_its_categories_only()
    {
        var access = Authorizer.GetAccess(User(roles: ["HelpDesk"]));

        Assert.False(access.IsAdmin);
        Assert.True(access.CanRun(new ScriptDefinition { CategoryId = "users" }));
        Assert.False(access.CanRun(new ScriptDefinition { CategoryId = "Admin" }));
    }

    [Fact]
    public void External_group_grants_access_case_insensitively()
    {
        var access = Authorizer.GetAccess(User(groups: ["HELPDESK-GROUP-ID"]));

        Assert.True(access.CanAccessCategory("Testing"));
        Assert.Equal(["HelpDesk"], access.Policies);
    }

    [Fact]
    public void Configured_admin_policy_sets_admin_flag_for_its_group()
    {
        var authorizer = new SpecOpsAuthorizer(
            TestOptions.Create(o =>
                o.Policies.Add(
                    new PolicyDefinition
                    {
                        Name = "Ops",
                        IsAdmin = true,
                        Groups = ["ops-admins"],
                        Categories = ["Ops"],
                    }
                )
            )
        );

        var access = authorizer.GetAccess(User(groups: ["ops-admins"]));

        Assert.True(access.IsAdmin);
        Assert.True(access.CanAccessCategory("Ops"));
        Assert.False(access.CanAccessCategory("Users"));
    }

    [Fact]
    public void Built_in_admin_role_is_admin_with_every_category_even_without_policies()
    {
        var access = new SpecOpsAuthorizer(TestOptions.Create()).GetAccess(User(roles: [SpecOpsAuthorizer.AdminRole]));

        Assert.True(access.IsAdmin);
        Assert.True(access.CanAccessCategory("anything"));
    }

    [Fact]
    public void Wildcard_grants_every_category()
    {
        Assert.True(Authorizer.GetAccess(User(roles: ["SuperUser"])).CanAccessCategory("anything"));
    }

    [Fact]
    public void Multiple_policies_combine()
    {
        var access = Authorizer.GetAccess(User(roles: ["HelpDesk", "SuperUser"]));

        Assert.True(access.AllCategories);
        Assert.Equal(["HelpDesk", "Everything"], access.Policies);
    }

    [Fact]
    public void MatchesAnyPolicy_supports_auto_approval_by_group()
    {
        Assert.True(Authorizer.MatchesAnyPolicy([], ["helpdesk-group-id"]));
        Assert.False(Authorizer.MatchesAnyPolicy([], ["some-other-group"]));
    }
}
