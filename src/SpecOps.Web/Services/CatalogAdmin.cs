using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;
using SpecOps.Core.Scripts;
using SpecOps.Web.Auth;

namespace SpecOps.Web.Services;

/// <summary>Outcome of a catalog change: the new version, or why it wasn't saved.</summary>
public sealed record CatalogSaveResult(long? Version, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Version is not null;

    public static CatalogSaveResult Fail(params string[] errors) => new(null, errors);
}

/// <summary>
/// What the Admin configuration pages call. Every change is validated as a whole (the same rules as specops.json at
/// startup), saved as a new catalog version and audited. Callers must already be admins; pages enforce that with the
/// Admin policy, and this checks again.
/// </summary>
public sealed class CatalogAdmin(
    CatalogStore store,
    IOptionsMonitor<SpecOpsOptions> options,
    IScriptCatalog catalog,
    AuditLog audit,
    Core.Security.ISpecOpsAuthorizer authorizer
)
{
    private static readonly SpecOpsOptionsValidator Validator = new();

    public long Version => store.Version;

    /// <summary>A copy of the live catalog to edit, with the version it came from (pass it back when saving).</summary>
    public (CatalogDocument Document, long Version) Load() => (store.GetDocument(), store.Version);

    /// <summary>Things worth fixing that don't stop the catalog working (e.g. a script file that doesn't exist).</summary>
    public IReadOnlyList<string> Warnings() => catalog.Validate();

    /// <summary>Validates and saves <paramref name="document"/> as the new live catalog.</summary>
    public async Task<CatalogSaveResult> SaveAsync(
        ClaimsPrincipal user,
        CatalogDocument document,
        long expectedVersion,
        string summary,
        string? clientIp = null
    )
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(document);
        if (!authorizer.GetAccess(user).IsAdmin)
        {
            return CatalogSaveResult.Fail("Only administrators can change the configuration.");
        }

        var validation = Validator.Validate(null, document.ToOptions(options.CurrentValue));
        if (validation.Failed)
        {
            return new CatalogSaveResult(null, [.. validation.Failures]);
        }

        var version = await store.SaveAsync(document, expectedVersion, summary, user.UserId(), user.DisplayName());
        if (version is null)
        {
            return CatalogSaveResult.Fail(
                "Someone else changed the configuration while you were editing. Reload the page and make your change again."
            );
        }

        await audit.RecordAsync(user, AuditActions.ConfigChanged, $"catalog v{version}", summary, clientIp);
        return new CatalogSaveResult(version, []);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the live catalog and saves it: for single-item edits (one script), which
    /// shouldn't fail just because someone changed something else in the meantime.
    /// </summary>
    public Task<CatalogSaveResult> ChangeAsync(ClaimsPrincipal user, string summary, Action<CatalogDocument> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var version = store.Version;
        var document = store.GetDocument();
        change(document);
        return SaveAsync(user, document, version, summary);
    }

    /// <summary>The problems that would stop <paramref name="document"/> being saved (empty if it's fine).</summary>
    public IReadOnlyList<string> Validate(CatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = Validator.Validate(null, document.ToOptions(options.CurrentValue));
        return result.Failed ? [.. result.Failures] : [];
    }

    /// <summary>Replaces the whole catalog with the contents of a specops.json-style file.</summary>
    public async Task<CatalogSaveResult> ImportAsync(ClaimsPrincipal user, string json, string fileName)
    {
        CatalogDocument document;
        try
        {
            document = CatalogDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return CatalogSaveResult.Fail($"{fileName} isn't valid configuration JSON: {ex.Message}");
        }

        return await SaveAsync(
            user,
            document,
            store.Version,
            $"Imported {fileName} ({document.Scripts.Count} scripts, {document.Policies.Count} policies)"
        );
    }

    /// <summary>Makes an earlier version live again (as a new version, so the restore itself can be undone).</summary>
    public async Task<CatalogSaveResult> RestoreAsync(ClaimsPrincipal user, long versionId)
    {
        var document = await store.GetVersionAsync(versionId);
        return document is null
            ? CatalogSaveResult.Fail($"Version {versionId} doesn't exist.")
            : await SaveAsync(user, document, store.Version, $"Restored version {versionId}");
    }

    public Task<IReadOnlyList<CatalogVersionInfo>> ListVersionsAsync(int take = 50) => store.ListVersionsAsync(take);

    public async Task<string?> ExportVersionAsync(long versionId) => (await store.GetVersionAsync(versionId))?.ToJson();
}
