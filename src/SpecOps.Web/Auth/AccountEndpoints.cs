using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpecOps.Data;

namespace SpecOps.Web.Auth;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/account").AllowAnonymous();

        account.MapPost("/external-login", async (
            HttpContext http,
            [FromForm] string provider,
            [FromForm] string? returnUrl,
            SignInManager<SpecOpsUser> signIn) =>
        {
            var schemes = await signIn.GetExternalAuthenticationSchemesAsync();
            if (!schemes.Any(s => s.Name == provider))
            {
                return Results.LocalRedirect("~/account/login?error=unknown-provider");
            }

            var callback = $"{http.Request.PathBase}/account/external-callback{QueryString.Create("returnUrl", SafeReturnUrl(returnUrl))}";
            var properties = signIn.ConfigureExternalAuthenticationProperties(provider, callback);
            return Results.Challenge(properties, [provider]);
        });

        account.MapGet("/external-callback", async (
            HttpContext http,
            string? returnUrl,
            string? remoteError,
            SignInManager<SpecOpsUser> signIn,
            ExternalSignInService externalSignIn) =>
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

            var (outcome, _) = await externalSignIn.SignInExternalAsync(info);
            await http.SignOutAsync(IdentityConstants.ExternalScheme);

            return outcome switch
            {
                SignInOutcome.SignedIn => Results.LocalRedirect(SafeReturnUrl(returnUrl)),
                SignInOutcome.PendingApproval => Results.LocalRedirect("~/account/pending"),
                SignInOutcome.Disabled => Results.LocalRedirect("~/account/denied"),
                _ => Results.LocalRedirect("~/account/login?error=failed"),
            };
        });

        account.MapPost("/dev-login", async (
            [FromForm] string? returnUrl,
            IOptions<AuthSettings> settings,
            IHostEnvironment environment,
            ExternalSignInService externalSignIn) =>
        {
            if (!settings.Value.DevSignIn || !environment.IsDevelopment())
            {
                return Results.NotFound();
            }

            await externalSignIn.SignInDevUserAsync();
            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        });

        account.MapPost("/logout", async (SignInManager<SpecOpsUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.LocalRedirect("~/account/login?signedOut=true");
        });

        return endpoints;
    }

    /// <summary>Only same-site paths; anything else (absolute URLs, protocol-relative "//host") goes home.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl[0] == '/' && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : "~/";
}
