namespace SpecOps.Data;

/// <summary>Who did what, when. Append-only record of sign-ins, admin changes and script/Terminal runs.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Identity user id of the actor, when known.</summary>
    public string? ActorId { get; set; }

    /// <summary>Display name or email of the actor at the time (kept even if the user is later renamed or deleted).</summary>
    public string Actor { get; set; } = "";

    /// <summary>Machine-readable action, e.g. "script.run", "user.approved". See AuditActions.</summary>
    public string Action { get; set; } = "";

    /// <summary>What the action applied to: a script id, a user, a run id.</summary>
    public string? Target { get; set; }

    /// <summary>Free-form details: parameters (sensitive values masked), Terminal code, role names.</summary>
    public string? Details { get; set; }

    public string? ClientIp { get; set; }
}
