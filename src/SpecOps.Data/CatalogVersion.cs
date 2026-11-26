namespace SpecOps.Data;

/// <summary>
/// One saved version of the script catalog (categories, policies, runspace profiles and scripts) as JSON. The newest
/// row is the live configuration; older rows are the change history admins can restore from.
/// </summary>
public sealed class CatalogVersion
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Identity user id of whoever saved it (null for the first-run import).</summary>
    public string? CreatedById { get; set; }

    /// <summary>Display name or email at the time.</summary>
    public string CreatedBy { get; set; } = "";

    /// <summary>What changed, e.g. "Updated script 'reset-password'".</summary>
    public string Summary { get; set; } = "";

    /// <summary>The whole catalog (SpecOps.Core.Configuration.CatalogDocument as JSON).</summary>
    public string Json { get; set; } = "";
}
