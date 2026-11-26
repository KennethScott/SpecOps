using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpecOps.Core.Security;
using SpecOps.Data;
using SpecOps.Web.Auth;
using SpecOps.Web.Services;

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

    private static ClaimsPrincipal Admin(string id = "admin-id") =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, id),
                    new Claim(ClaimTypes.Name, "Admin"),
                    new Claim(ClaimTypes.Role, SpecOpsAuthorizer.AdminRole),
                ],
                "test"
            )
        );

    [Fact]
    public async Task Admins_can_delete_users_who_then_start_over()
    {
        var subject = Unique();
        var first = await SignInAsync("Google", subject, ("email", $"{Unique()}@trusted.example"));
        Assert.Equal(UserStatus.Active, first.User.Status);

        var error = await factory.Services.GetRequiredService<UserAdmin>().DeleteAsync(Admin(), first.User.Id);

        Assert.Null(error);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
            Assert.Null(await db.Users.FindAsync([first.User.Id], TestContext.Current.CancellationToken));
            Assert.False(
                await db.UserLogins.AnyAsync(l => l.UserId == first.User.Id, TestContext.Current.CancellationToken)
            );
            var audit = await db.AuditEvents.SingleAsync(
                e => e.Action == AuditActions.UserDeleted && e.Details!.Contains(subject),
                TestContext.Current.CancellationToken
            );
            Assert.Equal("Admin", audit.Actor);
        }

        // The same person signing in again gets a brand-new account, waiting for approval like anyone new.
        var again = await SignInAsync("Google", subject, ("email", $"{Unique()}@contoso.com"));
        Assert.NotEqual(first.User.Id, again.User.Id);
        Assert.Equal(UserStatus.Pending, again.User.Status);
    }

    [Fact]
    public async Task Users_cant_delete_themselves_and_only_admins_can_delete()
    {
        var target = await SignInAsync("Microsoft", Unique(), ("email", $"{Unique()}@contoso.com"));
        var users = factory.Services.GetRequiredService<UserAdmin>();
        var helpDesk = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "x"), new Claim(ClaimTypes.Role, "HelpDesk")], "t")
        );

        Assert.NotNull(await users.DeleteAsync(Admin(target.User.Id), target.User.Id));
        Assert.NotNull(await users.DeleteAsync(helpDesk, target.User.Id));
        Assert.NotNull(await users.DeleteAsync(Admin(), "no-such-user"));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.NotNull(
            await scope
                .ServiceProvider.GetRequiredService<SpecOpsDbContext>()
                .Users.FindAsync([target.User.Id], TestContext.Current.CancellationToken)
        );
    }
}
