using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SpecOps.Core.Security;
using SpecOps.Data;

namespace SpecOps.Web.Auth;

public static class AuthenticationSetup
{
    public const string AdminPolicy = "SpecOpsAdmin";

    public const string DisplayNameClaim = "specops:display_name";

    private static readonly string[] DefaultScopes = ["openid", "profile", "email"];

    public static WebApplicationBuilder AddSpecOpsAuthentication(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(AuthSettings.SectionName);
        builder.Services.Configure<AuthSettings>(section);
        var settings = section.Get<AuthSettings>() ?? new AuthSettings();

        if (settings.DevSignIn && !builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Auth:DevSignIn is enabled outside the Development environment. It lets anyone sign in as an admin; turn it off.");
        }

        builder.Services
            .AddIdentityCore<SpecOpsUser>(options =>
            {
                options.User.AllowedUserNameCharacters = "";
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<SpecOpsDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<SpecOpsClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();

        var auth = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        });
        auth.AddIdentityCookies();

        foreach (var (name, provider) in settings.Providers.Where(p => p.Value.IsConfigured))
        {
            var displayName = provider.DisplayName ?? name;
            var callbackPath = $"/signin-{name.ToLowerInvariant()}";
            switch (provider.Type)
            {
                case ExternalProviderType.Google:
                    auth.AddGoogle(name, displayName, o =>
                    {
                        o.SignInScheme = IdentityConstants.ExternalScheme;
                        o.ClientId = provider.ClientId!;
                        o.ClientSecret = provider.ClientSecret ?? "";
                        o.CallbackPath = callbackPath;
                    });
                    break;

                default:
                    auth.AddOpenIdConnect(name, displayName, o =>
                    {
                        o.SignInScheme = IdentityConstants.ExternalScheme;
                        o.Authority = provider.Authority;
                        o.ClientId = provider.ClientId;
                        o.ClientSecret = provider.ClientSecret;
                        o.ResponseType = "code";
                        o.UsePkce = true;
                        o.CallbackPath = callbackPath;
                        o.SignedOutCallbackPath = $"/signout-callback-{name.ToLowerInvariant()}";
                        o.MapInboundClaims = false;
                        o.GetClaimsFromUserInfoEndpoint = true;
                        o.SaveTokens = false;
                        o.Scope.Clear();
                        foreach (var scope in DefaultScopes.Concat(provider.Scopes).Distinct())
                        {
                            o.Scope.Add(scope);
                        }

                        o.TokenValidationParameters.NameClaimType = "name";
                        // Keep the groups claim even when it only arrives in the id_token.
                        o.ClaimActions.Remove(provider.GroupsClaim);
                    });
                    break;
            }
        }

        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "SpecOps";
            options.LoginPath = "/account/login";
            options.AccessDeniedPath = "/account/denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
        });

        // Disabling a user or changing their roles takes effect within a minute (security stamp check).
        builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        builder.Services.AddSingleton<IAuthorizationHandler, AdminRequirementHandler>();
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().AddRequirements(new AdminRequirement()));

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider>();
        builder.Services.AddScoped<ExternalSignInService>();

        return builder;
    }
}

public sealed class AdminRequirement : IAuthorizationRequirement;

public sealed class AdminRequirementHandler(ISpecOpsAuthorizer authorizer) : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (authorizer.GetAccess(context.User).IsAdmin)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public sealed class SpecOpsClaimsPrincipalFactory(
    UserManager<SpecOpsUser> users,
    RoleManager<IdentityRole> roles,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<SpecOpsUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(SpecOpsUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AuthenticationSetup.DisplayNameClaim, user.Label));
        return identity;
    }
}

/// <summary>
/// Re-checks the user every minute while a Blazor circuit is open, so disabled users and role changes
/// take effect without waiting for the cookie to expire.
/// </summary>
public sealed class RevalidatingIdentityAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SpecOpsUser>>();
        var user = await users.GetUserAsync(authenticationState.User);
        if (user is null || user.Status != UserStatus.Active)
        {
            return false;
        }

        var principalStamp = authenticationState.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp == await users.GetSecurityStampAsync(user);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string DisplayName(this ClaimsPrincipal user) =>
        user?.FindFirstValue(AuthenticationSetup.DisplayNameClaim) ?? user?.Identity?.Name ?? "Unknown";

    public static string? UserId(this ClaimsPrincipal user) => user?.FindFirstValue(ClaimTypes.NameIdentifier);
}
