using System.Security.Claims;
using SpecOps.Web.Auth;

namespace SpecOps.Tests.Web;

public class ExternalIdentityTests
{
    private static readonly ExternalProviderSettings Entra = new() { Type = ExternalProviderType.OpenIdConnect };
    private static readonly ExternalProviderSettings Google = new() { Type = ExternalProviderType.Google };

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static ExternalIdentity Identity(ExternalProviderSettings settings, params (string, string)[] claims) =>
        ExternalIdentity.Create("Microsoft", "subject-123", Principal(claims), settings);

    [Fact]
    public void Oidc_email_without_email_verified_is_not_trusted()
    {
        var identity = Identity(Entra, ("email", "boss@contoso.com"));

        Assert.Equal("boss@contoso.com", identity.Email);
        Assert.Null(identity.TrustedEmail);
    }

    [Fact]
    public void Google_email_is_trusted_by_default()
    {
        Assert.NotNull(Identity(Google, ("email", "someone@gmail.com")).TrustedEmail);
    }

    [Fact]
    public void Explicit_email_verified_wins_over_provider_default()
    {
        Assert.Null(Identity(Google, ("email", "a@b.com"), ("email_verified", "false")).TrustedEmail);
        Assert.NotNull(Identity(Entra, ("email", "a@b.com"), ("email_verified", "true")).TrustedEmail);
    }

    [Fact]
    public void TrustEmail_setting_opts_an_oidc_provider_in()
    {
        var trusted = new ExternalProviderSettings { Type = ExternalProviderType.OpenIdConnect, TrustEmail = true };

        Assert.NotNull(Identity(trusted, ("email", "a@b.com")).TrustedEmail);
    }

    [Fact]
    public void Bootstrap_by_email_requires_a_trusted_email()
    {
        var settings = new AuthSettings { BootstrapAdmins = ["boss@contoso.com"] };

        Assert.False(Identity(Entra, ("email", "boss@contoso.com")).IsBootstrapAdmin(settings));
        Assert.True(
            Identity(Entra, ("email", "boss@contoso.com"), ("email_verified", "true")).IsBootstrapAdmin(settings)
        );
    }

    [Fact]
    public void Bootstrap_by_sign_in_id_works_for_any_provider()
    {
        var settings = new AuthSettings { BootstrapAdmins = ["Microsoft:subject-123"] };

        Assert.True(Identity(Entra).IsBootstrapAdmin(settings));
        Assert.False(
            ExternalIdentity.Create("Google", "subject-123", Principal(), Google).IsBootstrapAdmin(settings),
            "The provider is part of the ID"
        );
    }

    [Fact]
    public void Auto_approve_domain_requires_a_trusted_email()
    {
        var settings = new AuthSettings { AutoApproveEmailDomains = ["contoso.com"] };

        Assert.False(Identity(Entra, ("email", "new@contoso.com")).MatchesAutoApproveDomain(settings));
        Assert.True(Identity(Google, ("email", "new@contoso.com")).MatchesAutoApproveDomain(settings));
    }

    [Fact]
    public void Groups_come_from_the_configured_claim()
    {
        var settings = new ExternalProviderSettings { GroupsClaim = "roles" };

        var identity = Identity(settings, ("roles", "Ops"), ("roles", "ops"), ("groups", "ignored"));

        Assert.Equal(["Ops"], identity.Groups);
    }

    [Fact]
    public void Entra_upn_is_used_when_there_is_no_email_claim()
    {
        Assert.Equal("user@contoso.com", Identity(Entra, ("preferred_username", "user@contoso.com")).Email);
    }
}
