using System.Security.Claims;

namespace SpecOps.Web.Auth;

/// <summary>
/// What we learned about a person from an external sign-in, and how far we trust it.
/// Pure logic (no I/O) so the trust rules are easy to test.
/// </summary>
public sealed record ExternalIdentity(
    string Provider,
    string ProviderKey,
    string? Email,
    string? TrustedEmail,
    string? DisplayName,
    IReadOnlyList<string> Groups
)
{
    /// <summary>Stable, provider-scoped identifier, e.g. "Microsoft:3f2a…". Safe to use in BootstrapAdmins for any provider.</summary>
    public string SignInId => FormatSignInId(Provider, ProviderKey);

    public static string FormatSignInId(string provider, string providerKey) => $"{provider}:{providerKey}";

    public static ExternalIdentity Create(
        string provider,
        string providerKey,
        ClaimsPrincipal principal,
        ExternalProviderSettings? settings
    )
    {
        ArgumentNullException.ThrowIfNull(principal);
        var email = FindEmail(principal);
        var groupsClaim = settings?.GroupsClaim ?? "groups";
        var groups = principal
            .FindAll(groupsClaim)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ExternalIdentity(
            provider,
            providerKey,
            email,
            email is not null && IsEmailTrusted(principal, settings) ? email : null,
            principal.FindFirstValue("name") ?? principal.FindFirstValue(ClaimTypes.Name) ?? email,
            groups
        );
    }

    /// <summary>
    /// An email only counts for bootstrap/auto-approval when the provider says it's verified, or the provider is
    /// configured to be trusted (Google by default). OIDC providers such as Entra don't send email_verified and their
    /// email claim can be user-controlled, so they're untrusted unless explicitly configured otherwise.
    /// </summary>
    public static bool IsEmailTrusted(ClaimsPrincipal principal, ExternalProviderSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var verifiedClaim = principal.FindFirstValue("email_verified") ?? principal.FindFirstValue("verified_email");
        if (verifiedClaim is not null)
        {
            return string.Equals(verifiedClaim, "true", StringComparison.OrdinalIgnoreCase);
        }

        return settings?.EmailIsTrusted ?? false;
    }

    public bool IsBootstrapAdmin(AuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.BootstrapAdmins.Any(entry =>
            string.Equals(entry, SignInId, StringComparison.Ordinal)
            || (TrustedEmail is not null && string.Equals(entry, TrustedEmail, StringComparison.OrdinalIgnoreCase))
        );
    }

    public bool MatchesAutoApproveDomain(AuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var domain = TrustedEmail?.Split('@') is [_, var d] ? d : null;
        return domain is not null
            && settings.AutoApproveEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindEmail(ClaimsPrincipal principal)
    {
        var email = principal.FindFirstValue("email") ?? principal.FindFirstValue(ClaimTypes.Email);
        if (
            email is null
            && principal.FindFirstValue("preferred_username") is { } upn
            && upn.Contains('@', StringComparison.Ordinal)
        )
        {
            email = upn;
        }

        return email;
    }
}
