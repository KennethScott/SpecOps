using System.Data.Common;
using System.Security.Claims;
using SpecOps.Data;
using SpecOps.Web.Auth;

namespace SpecOps.Web.Services;

public static class AuditActions
{
    public const string SignIn = "signin";
    public const string SignInPending = "signin.pending";
    public const string SignInDenied = "signin.denied";
    public const string SignOut = "signout";
    public const string UserCreated = "user.created";
    public const string UserAutoApproved = "user.auto-approved";
    public const string BootstrapAdmin = "user.bootstrap-admin";
    public const string UserStatusChanged = "user.status";
    public const string RoleAdded = "user.role-added";
    public const string RoleRemoved = "user.role-removed";
    public const string ScriptRun = "script.run";
    public const string ScriptDenied = "script.denied";
    public const string TerminalRun = "terminal.run";
    public const string RunCancelled = "run.cancelled";
    public const string ConfigChanged = "config.changed";
    public const string ScriptFileCreated = "script-file.created";
    public const string ScriptFileSaved = "script-file.saved";
    public const string LogDownloaded = "log.downloaded";
}

/// <summary>
/// Durable audit trail (database) mirrored to the application log. Writing never throws: a failed audit write is
/// logged as an error rather than breaking the user's action.
/// </summary>
public sealed partial class AuditLog(IServiceScopeFactory scopes, ILogger<AuditLog> logger)
{
    public Task RecordAsync(
        ClaimsPrincipal actor,
        string action,
        string? target = null,
        string? details = null,
        string? clientIp = null
    )
    {
        ArgumentNullException.ThrowIfNull(actor);
        return RecordAsync(actor.UserId(), actor.DisplayName(), action, target, details, clientIp);
    }

    public async Task RecordAsync(
        string? actorId,
        string actor,
        string action,
        string? target = null,
        string? details = null,
        string? clientIp = null
    )
    {
        LogAudit(action, actor, target, details);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
            db.AuditEvents.Add(
                new AuditEvent
                {
                    ActorId = actorId,
                    Actor = Truncate(actor, 256) ?? "",
                    Action = action,
                    Target = Truncate(target, 256),
                    Details = details,
                    ClientIp = Truncate(clientIp, 64),
                }
            );
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            LogAuditWriteFailed(ex, action, actor);
        }
    }

    private static string? Truncate(string? value, int length) =>
        value is { Length: var l } && l > length ? value[..length] : value;

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit {Action} by {Actor} on {Target}: {Details}")]
    private partial void LogAudit(string action, string actor, string? target, string? details);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write audit event {Action} by {Actor}")]
    private partial void LogAuditWriteFailed(Exception ex, string action, string actor);
}
