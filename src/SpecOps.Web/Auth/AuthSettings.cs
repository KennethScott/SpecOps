namespace SpecOps.Web.Auth;

/// <summary>Bound from the "Auth" configuration section.</summary>
public sealed class AuthSettings
{
    public const string SectionName = "Auth";

    /// <summary>
    /// People who get the built-in Admin role (and are approved) when they sign in: first admin and lockout recovery.
    /// Each entry is either a sign-in ID ("Provider:subject", shown on the approval page; works with any provider)
    /// or an email address (only matches a verified/trusted email, see <see cref="ExternalProviderSettings.TrustEmail"/>).
    /// </summary>
    public IList<string> BootstrapAdmins { get; init; } = [];

    /// <summary>New users with a verified/trusted email in one of these domains are approved automatically (e.g. "contoso.com").</summary>
    public IList<string> AutoApproveEmailDomains { get; init; } = [];

    /// <summary>"Sign in as dev user" button. Development environment only; startup fails if enabled elsewhere.</summary>
    public bool DevSignIn { get; set; }

    /// <summary>External sign-in providers keyed by scheme name (e.g. "Microsoft", "Google", "Keycloak"). Providers without a ClientId are ignored.</summary>
    public IDictionary<string, ExternalProviderSettings> Providers { get; init; } =
        new Dictionary<string, ExternalProviderSettings>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Problems that would make sign-in fail or behave unsafely; checked at startup.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        foreach (var (name, provider) in Providers.Where(p => p.Value.IsConfigured))
        {
            var label = $"Auth:Providers:{name}";
            if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                errors.Add(
                    $"{label}: provider names may only contain letters, digits, '-' and '_' (used in callback URLs)."
                );
            }

            if (string.IsNullOrWhiteSpace(provider.ClientSecret))
            {
                errors.Add($"{label}: ClientSecret is required.");
            }

            if (provider.Type == ExternalProviderType.OpenIdConnect)
            {
                if (
                    !Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority)
                    || (authority.Scheme != Uri.UriSchemeHttps && !authority.IsLoopback)
                )
                {
                    errors.Add($"{label}: Authority must be an https URL (got '{provider.Authority}').");
                }

                if (provider.Authority?.Contains('<', StringComparison.Ordinal) == true)
                {
                    errors.Add($"{label}: replace the placeholder in Authority with your tenant ID.");
                }
            }

            if (string.IsNullOrWhiteSpace(provider.GroupsClaim))
            {
                errors.Add($"{label}: GroupsClaim can't be empty.");
            }

            foreach (var tenant in provider.AllowedTenantIds.Where(t => !Guid.TryParse(t, out _)))
            {
                errors.Add($"{label}: AllowedTenantIds entry '{tenant}' isn't a tenant ID (GUID).");
            }
        }

        if (BootstrapAdmins.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("Auth:BootstrapAdmins contains an empty entry.");
        }

        foreach (var domain in AutoApproveEmailDomains.Where(d => string.IsNullOrWhiteSpace(d) || d.Contains('@')))
        {
            errors.Add($"Auth:AutoApproveEmailDomains entry '{domain}' should be a bare domain like 'contoso.com'.");
        }

        return errors;
    }
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

    /// <summary>
    /// Whether this provider's email claim can be trusted when it doesn't send email_verified. Defaults to true for
    /// Google (verified accounts only) and false for OpenID Connect. Only enable for an OIDC provider whose emails
    /// users can't change, e.g. Entra restricted to your tenant via <see cref="AllowedTenantIds"/>.
    /// </summary>
    public bool? TrustEmail { get; set; }

    /// <summary>Entra ID: only accept sign-ins whose "tid" claim is one of these tenant IDs. Strongly recommended.</summary>
    public IList<string> AllowedTenantIds { get; init; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public bool EmailIsTrusted => TrustEmail ?? Type == ExternalProviderType.Google;
}
