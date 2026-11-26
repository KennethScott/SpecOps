using SpecOps.Web.Auth;
using SpecOps.Web.Services;

namespace SpecOps.Web.Infrastructure;

public static class LogEndpoints
{
    /// <summary>GET /admin/logs/{name}: downloads a whole log file (admins only, audited).</summary>
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet("/admin/logs/{name}", DownloadAsync)
            .RequireAuthorization(AuthenticationSetup.AdminPolicy)
            .WithName("DownloadLog");
        return endpoints;
    }

    private static async Task<IResult> DownloadAsync(string name, LogFiles logs, AuditLog audit, HttpContext context)
    {
        var stream = logs.Open(name);
        if (stream is null)
        {
            return TypedResults.NotFound();
        }

        await audit.RecordAsync(context.User, AuditActions.LogDownloaded, name, clientIp: context.ClientIp());
        return TypedResults.Stream(stream, "text/plain; charset=utf-8", name);
    }
}
