using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpecOps.Core.Security;
using SpecOps.Data;
using SpecOps.Web.Auth;

namespace SpecOps.Tests.Web;

/// <summary>Host configured with a bootstrap admin ID and an auto-approve domain.</summary>
public sealed class SignInTestFactory : SpecOpsWebFactory
{
    public const string BootstrapId = "Microsoft:boss-subject";

    // The sample specops.json grants "Help desk" to this Entra group.
    public const string HelpDeskGroup = "00000000-0000-0000-0000-000000000000";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:BootstrapAdmins:0", BootstrapId);
        builder.UseSetting("Auth:AutoApproveEmailDomains:0", "trusted.example");
    }
}

/// <summary>The real ExternalSignInService against Identity + SQLite: provisioning, approval rules, group sync.</summary>
public sealed class ExternalSignInTests(SignInTestFactory factory) : IClassFixture<SignInTestFactory>
{
    private sealed record Outcome(
        ExternalSignInResult Result,
        SpecOpsUser User,
        IList<string> Roles,
        IList<Claim> Claims
    );

    private async Task<Outcome> SignInAsync(
        string provider,
        string subject,
        params (string Type, string Value)[] claims
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        // SignInManager writes the cookie through the current HttpContext.
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), provider)
        );
        var result = await services
            .GetRequiredService<ExternalSignInService>()
            .SignInExternalAsync(new ExternalLoginInfo(principal, provider, subject, provider), "10.1.2.3");

        var users = services.GetRequiredService<UserManager<SpecOpsUser>>();
        var user = (await users.FindByLoginAsync(provider, subject))!;
        return new Outcome(result, user, await users.GetRolesAsync(user), await users.GetClaimsAsync(user));
    }

    private static string Unique() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task New_users_wait_for_approval()
    {
        var outcome = await SignInAsync(
            "Microsoft",
            Unique(),
            ("email", $"{Unique()}@contoso.com"),
            ("name", "New Person")
        );

        Assert.Equal(SignInOutcome.PendingApproval, outcome.Result.Outcome);
        Assert.Equal(UserStatus.Pending, outcome.User.Status);
        Assert.Equal("New Person", outcome.User.DisplayName);
        Assert.StartsWith("Microsoft:", outcome.Result.SignInId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_sign_in_id_becomes_an_active_admin()
    {
        var outcome = await SignInAsync("Microsoft", "boss-subject", ("email", "boss@contoso.com"));

        Assert.Equal(SignInOutcome.SignedIn, outcome.Result.Outcome);
        Assert.Equal(UserStatus.Active, outcome.User.Status);
        Assert.Contains(SpecOpsAuthorizer.AdminRole, outcome.Roles);
    }

    [Fact]
    public async Task Unverified_entra_email_does_not_auto_approve_but_a_policy_group_does()
    {
        var unverified = await SignInAsync("Microsoft", Unique(), ("email", $"{Unique()}@trusted.example"));
        var grouped = await SignInAsync(
            "Microsoft",
            Unique(),
            ("email", "x@contoso.com"),
            ("groups", SignInTestFactory.HelpDeskGroup)
        );

        Assert.Equal(SignInOutcome.PendingApproval, unverified.Result.Outcome);
        Assert.Equal(SignInOutcome.SignedIn, grouped.Result.Outcome);
    }

    [Fact]
    public async Task Verified_email_in_an_auto_approve_domain_is_approved()
    {
        var outcome = await SignInAsync(
            "Google",
            Unique(),
            ("email", $"{Unique()}@trusted.example"),
            ("email_verified", "true")
        );

        Assert.Equal(SignInOutcome.SignedIn, outcome.Result.Outcome);
    }

    [Fact]
    public async Task Group_memberships_are_refreshed_on_every_sign_in()
    {
        var subject = Unique();
        await SignInAsync("Microsoft", subject, ("groups", "group-a"), ("groups", "group-b"));

        var second = await SignInAsync("Microsoft", subject, ("groups", "group-b"), ("groups", "group-c"));

        Assert.Equal(
            ["group-b", "group-c"],
            second.Claims.Where(c => c.Type == SpecOpsAuthorizer.GroupClaimType).Select(c => c.Value).Order()
        );
    }

    [Fact]
    public async Task Disabled_users_are_turned_away_and_audited()
    {
        var subject = Unique();
        var first = await SignInAsync("Microsoft", subject);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<SpecOpsUser>>();
            var user = (await users.FindByIdAsync(first.User.Id))!;
            user.Status = UserStatus.Disabled;
            await users.UpdateAsync(user);
        }

        var second = await SignInAsync("Microsoft", subject);

        Assert.Equal(SignInOutcome.Disabled, second.Result.Outcome);
        await using var check = factory.Services.CreateAsyncScope();
        var audit = await check
            .ServiceProvider.GetRequiredService<SpecOpsDbContext>()
            .AuditEvents.AsNoTracking()
            .Where(e => e.ActorId == first.User.Id)
            .Select(e => e.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("signin.denied", audit);
        Assert.Contains("user.created", audit);
    }
}
