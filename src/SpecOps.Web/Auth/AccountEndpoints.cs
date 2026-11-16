using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpecOps.Data;
using SpecOps.Web.Infrastructure;
using SpecOps.Web.Services;

namespace SpecOps.Web.Auth;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/account").AllowAnonymous().RequireRateLimiting(RateLimiting.AuthPolicy);

        account.MapPost(
            "/external-login",
            async (
                HttpContext http,
                [FromForm] string provider,
                [FromForm] string? returnUrl,
                SignInManager<SpecOpsUser> signIn
            ) =>
            {
                var schemes = await signIn.GetExternalAuthenticationSchemesAsync();
                if (!schemes.Any(s => s.Name == provider))
                {
                    return Results.LocalRedirect("~/account/login?error=unknown-provider");
                }

                var callback =
                    $"{http.Request.PathBase}/account/external-callback{QueryString.Create("returnUrl", SafeReturnUrl(returnUrl))}";
                var properties = signIn.ConfigureExternalAuthenticationProperties(provider, callback);
                return Results.Challenge(properties, [provider]);
            }
        );

        account.MapGet(
            "/external-callback",
            async (
                HttpContext http,
                string? returnUrl,
                string? remoteError,
                SignInManager<SpecOpsUser> signIn,
                ExternalSignInService externalSignIn
            ) =>
            {
                if (remoteError is not null)
                {
                    return Results.LocalRedirect("~/account/login?error=provider");
                }

                var info = await signIn.GetExternalLoginInfoAsync();
                if (info is null)
                {
                    return Results.LocalRedirect("~/account/login?error=no-login-info");
                }

                var result = await externalSignIn.SignInExternalAsync(info, http.ClientIp());
                await http.SignOutAsync(IdentityConstants.ExternalScheme);

                return result.Outcome switch
                {
                    SignInOutcome.SignedIn => Results.LocalRedirect(SafeReturnUrl(returnUrl)),
                    // The sign-in ID lets an admin add this person to Auth:BootstrapAdmins or find them on the Users page.
                    SignInOutcome.PendingApproval => Results.LocalRedirect(
                        $"~/account/pending{QueryString.Create("id", result.SignInId ?? "")}"
                    ),
                    SignInOutcome.Disabled => Results.LocalRedirect("~/account/denied"),
                    _ => Results.LocalRedirect("~/account/login?error=failed"),
                };
            }
        );

        account.MapPost(
            "/dev-login",
            async (
                HttpContext http,
                [FromForm] string? returnUrl,
                IOptions<AuthSettings> settings,
                IHostEnvironment environment,
                ExternalSignInService externalSignIn
            ) =>
            {
                if (!settings.Value.DevSignIn || !environment.IsDevelopment())
                {
                    return Results.NotFound();
                }

                await externalSignIn.SignInDevUserAsync(http.ClientIp());
                return Results.LocalRedirect(SafeReturnUrl(returnUrl));
            }
        );

        account.MapPost("/logout", async (HttpContext http, SignInManager<SpecOpsUser> signIn, AuditLog audit) =>
        {
            if (http.User.Identity?.IsAuthenticated == true)
            {
                await audit.RecordAsync(http.User, AuditActions.SignOut, clientIp: http.ClientIp());
            }

            await signIn.SignOutAsync();
            return Results.LocalRedirect("~/account/login?signedOut=true");
        });

        return endpoints;
    }

    /// <summary>Only same-site paths; anything else (absolute URLs, protocol-relative "//host") goes home.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : "~/";
}
