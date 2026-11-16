using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpecOps.Data;
using SpecOps.Web.Infrastructure;
using SpecOps.Web.Services;

namespace SpecOps.Web.Auth;

/// <summary>
/// Sign-in/out endpoints. Handlers return TypedResults with declared result types, so the possible outcomes of each
/// endpoint are visible in its signature (and to OpenAPI/tests).
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/account").AllowAnonymous().RequireRateLimiting(RateLimiting.AuthPolicy);

        account.MapPost("/external-login", ExternalLoginAsync);
        account.MapGet("/external-callback", ExternalCallbackAsync);
        account.MapPost("/dev-login", DevLoginAsync);
        account.MapPost("/logout", LogoutAsync);

        return endpoints;
    }

    /// <summary>Starts the challenge with the chosen identity provider.</summary>
    internal static async Task<Results<ChallengeHttpResult, RedirectHttpResult>> ExternalLoginAsync(
        HttpContext http,
        [FromForm] string provider,
        [FromForm] string? returnUrl,
        SignInManager<SpecOpsUser> signIn
    )
    {
        var schemes = await signIn.GetExternalAuthenticationSchemesAsync();
        if (!schemes.Any(s => s.Name == provider))
        {
            return TypedResults.LocalRedirect("~/account/login?error=unknown-provider");
        }

        var callback =
            $"{http.Request.PathBase}/account/external-callback{QueryString.Create("returnUrl", SafeReturnUrl(returnUrl))}";
        var properties = signIn.ConfigureExternalAuthenticationProperties(provider, callback);
        return TypedResults.Challenge(properties, [provider]);
    }

    /// <summary>Where the identity provider sends the user back: provision/refresh the user and sign them in.</summary>
    internal static async Task<RedirectHttpResult> ExternalCallbackAsync(
        HttpContext http,
        string? returnUrl,
        string? remoteError,
        SignInManager<SpecOpsUser> signIn,
        ExternalSignInService externalSignIn
    )
    {
        if (remoteError is not null)
        {
            return TypedResults.LocalRedirect("~/account/login?error=provider");
        }

        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null)
        {
            return TypedResults.LocalRedirect("~/account/login?error=no-login-info");
        }

        var result = await externalSignIn.SignInExternalAsync(info, http.ClientIp());
        await http.SignOutAsync(IdentityConstants.ExternalScheme);

        return result.Outcome switch
        {
            SignInOutcome.SignedIn => TypedResults.LocalRedirect(SafeReturnUrl(returnUrl)),
            // The sign-in ID lets an admin add this person to Auth:BootstrapAdmins or find them on the Users page.
            SignInOutcome.PendingApproval => TypedResults.LocalRedirect(
                $"~/account/pending{QueryString.Create("id", result.SignInId ?? "")}"
            ),
            SignInOutcome.Disabled => TypedResults.LocalRedirect("~/account/denied"),
            _ => TypedResults.LocalRedirect("~/account/login?error=failed"),
        };
    }

    /// <summary>Development only: sign in as an admin without an identity provider.</summary>
    internal static async Task<Results<RedirectHttpResult, NotFound>> DevLoginAsync(
        HttpContext http,
        [FromForm] string? returnUrl,
        IOptions<AuthSettings> settings,
        IHostEnvironment environment,
        ExternalSignInService externalSignIn
    )
    {
        if (!settings.Value.DevSignIn || !environment.IsDevelopment())
        {
            return TypedResults.NotFound();
        }

        await externalSignIn.SignInDevUserAsync(http.ClientIp());
        return TypedResults.LocalRedirect(SafeReturnUrl(returnUrl));
    }

    internal static async Task<RedirectHttpResult> LogoutAsync(
        HttpContext http,
        SignInManager<SpecOpsUser> signIn,
        AuditLog audit
    )
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            await audit.RecordAsync(http.User, AuditActions.SignOut, clientIp: http.ClientIp());
        }

        await signIn.SignOutAsync();
        return TypedResults.LocalRedirect("~/account/login?signedOut=true");
    }

    /// <summary>Only same-site paths; anything else (absolute URLs, protocol-relative "//host") goes home.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : "~/";
}
