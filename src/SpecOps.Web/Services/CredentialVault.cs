using System.Management.Automation;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;
using SpecOps.Data;
using SpecOps.Web.Auth;

namespace SpecOps.Web.Services;

/// <summary>A stored credential as the UI sees it: everything except the password.</summary>
public sealed record CredentialInfo(
    string Id,
    string Name,
    string UserName,
    string? Description,
    DateTime UpdatedAtUtc,
    string UpdatedBy
);

/// <summary>
/// Named credentials (user name + password) that admins store once and scripts receive as a PSCredential, so
/// passwords never live in scripts. Passwords are encrypted with Data Protection, are never shown again after
/// saving, and never appear in logs, run history or the audit log. Only admins can add, change or delete them;
/// which scripts may use which credential is set on the script's Credential parameter (also admin-only).
/// </summary>
public sealed partial class CredentialVault(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    ISpecOpsAuthorizer authorizer,
    AuditLog audit,
    TimeProvider time
)
{
    public const int MaxPasswordLength = 1024;

    private readonly IDataProtector _protector = dataProtection.CreateProtector("SpecOps.CredentialVault.v1");

    public async Task<IReadOnlyList<CredentialInfo>> ListAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        var rows = await db.Credentials.AsNoTracking().ToListAsync();
        return
        [
            .. rows.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new CredentialInfo(c.Id, c.Name, c.UserName, c.Description, c.UpdatedAtUtc, c.UpdatedBy)),
        ];
    }

    /// <summary>
    /// Adds (<paramref name="isNew"/>) or updates a credential. A null/empty <paramref name="password"/> keeps the
    /// current one when updating. Returns the problems, empty when saved.
    /// </summary>
    public async Task<IReadOnlyList<string>> SaveAsync(
        ClaimsPrincipal actor,
        CredentialInfo credential,
        string? password,
        bool isNew,
        string? clientIp = null
    )
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(credential);
        if (!authorizer.GetAccess(actor).IsAdmin)
        {
            return ["Only administrators can manage credentials."];
        }

        var id = credential.Id.Trim();
        var name = credential.Name.Trim();
        var userName = credential.UserName.Trim();
        var errors = new List<string>();
        if (!IdRegex().IsMatch(id) || id.Length > 128)
        {
            errors.Add("Id: letters, digits, '.', '_' and '-' only (up to 128).");
        }

        if (name.Length is 0 or > 256)
        {
            errors.Add("Name is required (up to 256 characters).");
        }

        if (userName.Length is 0 or > 256)
        {
            errors.Add("User name is required (up to 256 characters).");
        }

        if (isNew && string.IsNullOrEmpty(password))
        {
            errors.Add("Password is required.");
        }

        if (password?.Length > MaxPasswordLength)
        {
            errors.Add($"Password must be {MaxPasswordLength} characters or fewer.");
        }

        if (credential.Description?.Length > 1024)
        {
            errors.Add("Description must be 1024 characters or fewer.");
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        var row = await db.Credentials.FindAsync(id);
        if (isNew == row is not null)
        {
            return [isNew ? $"A credential with id '{id}' already exists." : "That credential no longer exists."];
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = new VaultCredential { Id = id, CreatedAtUtc = now };
            db.Credentials.Add(row);
        }

        var changes = new List<string>();
        if (!string.Equals(row.UserName, userName, StringComparison.Ordinal))
        {
            changes.Add($"user name: {userName}");
        }

        row.Name = name;
        row.UserName = userName;
        row.Description = string.IsNullOrWhiteSpace(credential.Description) ? null : credential.Description.Trim();
        row.UpdatedAtUtc = now;
        row.UpdatedBy = actor.DisplayName();
        if (!string.IsNullOrEmpty(password))
        {
            row.ProtectedPassword = _protector.Protect(password);
            changes.Add("password set");
        }

        await db.SaveChangesAsync();
        await audit.RecordAsync(
            actor,
            isNew ? AuditActions.CredentialCreated : AuditActions.CredentialUpdated,
            id,
            changes.Count == 0 ? "name/description" : string.Join("; ", changes),
            clientIp
        );
        return [];
    }

    /// <summary>Returns an error message, or null when deleted.</summary>
    public async Task<string?> DeleteAsync(
        ClaimsPrincipal actor,
        string id,
        IEnumerable<ScriptDefinition> scripts,
        string? clientIp = null
    )
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!authorizer.GetAccess(actor).IsAdmin)
        {
            return "Only administrators can manage credentials.";
        }

        if (UsedBy(id, scripts) is { Count: > 0 } users)
        {
            return $"'{id}' is used by {string.Join(", ", users.Select(s => s.Name))}; remove it from those scripts first.";
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        if (await db.Credentials.FindAsync(id) is not { } row)
        {
            return "That credential no longer exists.";
        }

        db.Credentials.Remove(row);
        await db.SaveChangesAsync();
        await audit.RecordAsync(actor, AuditActions.CredentialDeleted, id, row.UserName, clientIp);
        return null;
    }

    /// <summary>Scripts with a Credential parameter that offers this credential.</summary>
    public static IReadOnlyList<ScriptDefinition> UsedBy(string id, IEnumerable<ScriptDefinition> scripts) =>
        [
            .. scripts.Where(s =>
                s.Parameters.Any(p =>
                    p.Type == ParameterType.Credential
                    && p.Options.Any(o => string.Equals(o.Value, id, StringComparison.OrdinalIgnoreCase))
                )
            ),
        ];

    /// <summary>The decrypted credential for a run; null if it doesn't exist. Callers check the script allows it.</summary>
    public async Task<PSCredential?> GetCredentialAsync(string id)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        if (await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id) is not { } row)
        {
            return null;
        }

        var password = new NetworkCredential("", _protector.Unprotect(row.ProtectedPassword)).SecurePassword;
        password.MakeReadOnly();
        return new PSCredential(row.UserName, password);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdRegex();
}
