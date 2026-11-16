namespace SpecOps.Web.Infrastructure;

public static class HttpContextExtensions
{
    /// <summary>The caller's IP after forwarded-headers processing (i.e. the real client when behind a trusted proxy).</summary>
    public static string? ClientIp(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Connection.RemoteIpAddress?.ToString();
    }
}
