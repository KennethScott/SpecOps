using SpecOps.Web.Auth;

namespace SpecOps.Tests.Web;

public class AuthSettingsTests
{
    private static AuthSettings With(string name, ExternalProviderSettings provider) =>
        new() { Providers = { [name] = provider } };

    [Fact]
    public void Unconfigured_providers_are_ignored()
    {
        var settings = With("Microsoft", new ExternalProviderSettings { Authority = "<tenant-id>" });

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void A_complete_entra_provider_is_valid()
    {
        var settings = With(
            "Microsoft",
            new ExternalProviderSettings
            {
                Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
                ClientId = "id",
                ClientSecret = "secret",
                AllowedTenantIds = ["11111111-1111-1111-1111-111111111111"],
            }
        );

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Reports_placeholders_missing_secrets_and_bad_tenant_ids()
    {
        var settings = With(
            "Microsoft",
            new ExternalProviderSettings
            {
                Authority = "https://login.microsoftonline.com/<tenant-id>/v2.0",
                ClientId = "id",
                AllowedTenantIds = ["contoso"],
            }
        );

        var errors = settings.Validate();

        Assert.Contains(errors, e => e.Contains("ClientSecret", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("placeholder", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'contoso'", StringComparison.Ordinal));
    }

    [Fact]
    public void Oidc_authority_must_be_https()
    {
        var settings = With(
            "Keycloak",
            new ExternalProviderSettings
            {
                Authority = "http://sso.example.com/realms/ops",
                ClientId = "id",
                ClientSecret = "s",
            }
        );

        Assert.Contains(settings.Validate(), e => e.Contains("https", StringComparison.Ordinal));
    }

    [Fact]
    public void Auto_approve_domains_must_be_bare_domains()
    {
        var settings = new AuthSettings { AutoApproveEmailDomains = ["@contoso.com"] };

        Assert.Single(settings.Validate());
    }
}
