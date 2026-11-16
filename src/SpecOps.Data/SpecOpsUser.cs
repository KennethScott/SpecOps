using Microsoft.AspNetCore.Identity;

namespace SpecOps.Data;

public enum UserStatus
{
    /// <summary>Signed in at least once; waiting for an admin to approve.</summary>
    Pending,
    Active,
    Disabled,
}

/// <summary>
/// A person who has signed in with an external provider (Entra ID, Google, OIDC, Windows).
/// There are no local passwords; logins are linked through AspNetUserLogins.
/// </summary>
public sealed class SpecOpsUser : IdentityUser
{
    public string? DisplayName { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Pending;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSignInAtUtc { get; set; }

    public string Label => DisplayName ?? Email ?? UserName ?? Id;
}
