namespace SpecOps.Data;

/// <summary>
/// A stored credential (user name + password) scripts can be given as a PSCredential. The password is encrypted with
/// ASP.NET Core Data Protection (keys in the data directory), never stored or shown in plain text.
/// </summary>
public sealed class VaultCredential
{
    /// <summary>Stable id referenced by script parameters, e.g. "ad-service".</summary>
    public string Id { get; set; } = "";

    /// <summary>Friendly name shown in the run form, e.g. "AD service account".</summary>
    public string Name { get; set; } = "";

    public string UserName { get; set; } = "";

    /// <summary>The password, encrypted (Data Protection payload).</summary>
    public string ProtectedPassword { get; set; } = "";

    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Display name or email of whoever last changed it.</summary>
    public string UpdatedBy { get; set; } = "";
}
