namespace SpecOps.Web.Auth;

/// <summary>Bound from the "Auth" configuration section.</summary>
public sealed class AuthSettings
{
    public const string SectionName = "Auth";

    /// <summary>Emails that get the built-in Admin role (and are approved) when they sign in. Use for the first admin and lockout recovery.</summary>
    public IList<string> BootstrapAdmins { get; init; } = [];

    /// <summary>New users with a verified email in one of these domains are approved automatically (e.g. "contoso.com").</summary>
    public IList<string> AutoApproveEmailDomains { get; init; } = [];

    /// <summary>"Sign in as dev user" button. Development environment only; startup fails if enabled elsewhere.</summary>
    public bool DevSignIn { get; set; }

    /// <summary>External sign-in providers keyed by scheme name (e.g. "Microsoft", "Google", "Keycloak"). Providers without a ClientId are ignored.</summary>
    public IDictionary<string, ExternalProviderSettings> Providers { get; init; } =
        new Dictionary<string, ExternalProviderSettings>(StringComparer.OrdinalIgnoreCase);
}

public enum ExternalProviderType
{
    OpenIdConnect,
    Google,
}

public sealed class ExternalProviderSettings
{
    public ExternalProviderType Type { get; set; } = ExternalProviderType.OpenIdConnect;

    /// <summary>Button text on the login page; defaults to the provider key.</summary>
    public string? DisplayName { get; set; }

    /// <summary>OIDC authority, e.g. https://login.microsoftonline.com/{tenant-id}/v2.0 (use your tenant id, not "common").</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Claim carrying group memberships (Entra: "groups" with group object IDs, or "roles" for app roles).</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>Extra scopes beyond openid/profile/email.</summary>
    public IList<string> Scopes { get; init; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
