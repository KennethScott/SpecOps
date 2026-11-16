namespace SpecOps.Data;

/// <summary>
/// One script or Terminal run, kept after it leaves the runner's memory. Written when the run is queued and again
/// when it finishes (with its output and results); see Runner.HistoryRetention for how long rows are kept.
/// </summary>
public sealed class RunRecord
{
    public Guid Id { get; set; }

    public string ScriptId { get; set; } = "";

    /// <summary>Script name at the time of the run (kept if the script is later renamed or removed).</summary>
    public string ScriptName { get; set; } = "";

    /// <summary>Identity user id of whoever started the run, when known.</summary>
    public string? UserId { get; set; }

    /// <summary>Display name or email at the time of the run.</summary>
    public string UserName { get; set; } = "";

    public string? ClientIp { get; set; }

    /// <summary>Parameter values as a JSON object, with sensitive values already masked.</summary>
    public string ParametersJson { get; set; } = "{}";

    /// <summary>The ad-hoc code for Terminal runs.</summary>
    public string? Code { get; set; }

    public DateTime QueuedAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    /// <summary>Null while the run is queued or running.</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>A SpecOps.Core.Execution.RunStatus name.</summary>
    public string Status { get; set; } = "";

    public int ErrorCount { get; set; }

    public int WarningCount { get; set; }

    public int ResultCount { get; set; }

    public bool ResultsTruncated { get; set; }

    /// <summary>The console output (JSON array of records); set when the run finishes.</summary>
    public string? OutputJson { get; set; }

    /// <summary>Objects the script emitted (JSON array of rows); set when the run finishes.</summary>
    public string? ResultsJson { get; set; }
}
