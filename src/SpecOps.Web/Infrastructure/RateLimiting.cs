using System.Threading.RateLimiting;

namespace SpecOps.Web.Infrastructure;

/// <summary>
/// HTTP rate limits. Script runs are limited per user inside the runner (they don't arrive as HTTP requests),
/// see RunnerOptions.MaxActiveRunsPerUser / MaxRunsPerMinutePerUser.
/// </summary>
public static class RateLimiting
{
    /// <summary>Sign-in, callback, dev sign-in and sign-out endpoints: per client IP.</summary>
    public const string AuthPolicy = "auth";

    public static IServiceCollection AddSpecOpsRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.ClientIp() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }
                )
            );
        });
}
