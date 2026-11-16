using SpecOps.Web.Auth;

namespace SpecOps.Web.Infrastructure;

/// <summary>
/// Security headers (NetEscapades.AspNetCore.SecurityHeaders): a nonce-based Content-Security-Policy plus the usual
/// hardening headers. Inline scripts must carry the request's nonce (see App.razor).
/// </summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSpecOpsSecurityHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var settings = app.Configuration.GetSection(AuthSettings.SectionName).Get<AuthSettings>() ?? new AuthSettings();
        var isDevelopment = app.Environment.IsDevelopment();
        var signInOrigins = SignInOrigins(settings).ToArray();

        var policies = new HeaderPolicyCollection()
            .AddFrameOptionsDeny()
            .AddContentTypeOptionsNoSniff()
            .AddReferrerPolicyStrictOriginWhenCrossOrigin()
            .AddCrossOriginOpenerPolicy(b => b.SameOrigin())
            .RemoveServerHeader()
            // Only sent over HTTPS by the library; one year, including subdomains.
            .AddStrictTransportSecurityMaxAgeIncludeSubDomains(maxAgeInSeconds: 60 * 60 * 24 * 365)
            .AddPermissionsPolicy(b =>
            {
                b.AddCamera().None();
                b.AddMicrophone().None();
                b.AddGeolocation().None();
                b.AddPayment().None();
                b.AddUsb().None();
            })
            .AddContentSecurityPolicy(csp =>
            {
                csp.AddDefaultSrc().Self();
                csp.AddObjectSrc().None();
                csp.AddBaseUri().Self();
                csp.AddFrameAncestors().None();

                // Our inline scripts (theme, Monaco loader config, Blazor import map) carry the per-request nonce.
                csp.AddScriptSrc().Self().WithNonce();
                // Monaco and Blazor (e.g. Virtualize spacers) set inline styles.
                csp.AddStyleSrc().Self().UnsafeInline();
                csp.AddImgSrc().Self().Data();
                csp.AddFontSrc().Self().Data();
                // Monaco language services run in web workers.
                csp.AddWorkerSrc().Self().Blob();

                var connect = csp.AddConnectSrc().Self();
                if (isDevelopment)
                {
                    // Hot reload / browser refresh talk to the dev tooling on another localhost port.
                    connect.From("ws://localhost:*").From("wss://localhost:*").From("http://localhost:*");
                }

                // The sign-in form posts here and is then redirected to the identity provider; browsers check
                // form-action against that redirect too.
                var formAction = csp.AddFormAction().Self();
                foreach (var origin in signInOrigins)
                {
                    formAction.From(origin);
                }
            });

        return app.UseSecurityHeaders(policies);
    }

    private static IEnumerable<string> SignInOrigins(AuthSettings settings)
    {
        foreach (var provider in settings.Providers.Values.Where(p => p.IsConfigured))
        {
            if (provider.Type == ExternalProviderType.Google)
            {
                yield return "https://accounts.google.com";
            }
            else if (Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority))
            {
                yield return authority.GetLeftPart(UriPartial.Authority);
            }
        }
    }
}
