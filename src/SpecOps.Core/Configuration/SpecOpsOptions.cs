using SpecOps.Core.Scripts;
using SpecOps.Core.Security;

namespace SpecOps.Core.Configuration;

/// <summary>
/// Root configuration for scripts, categories, security policies and the runner.
/// Bound from the "SpecOps" configuration section (specops.json), reloaded on change.
/// </summary>
public sealed class SpecOpsOptions
{
    public const string SectionName = "SpecOps";

    /// <summary>Folder that relative script paths are resolved against.</summary>
    public string ScriptsRoot { get; set; } = "scripts";

    public RunnerOptions Runner { get; set; } = new();

    public IList<CategoryDefinition> Categories { get; init; } = [];

    public IList<PolicyDefinition> Policies { get; init; } = [];

    public IList<RunspaceProfile> RunspaceProfiles { get; init; } = [];

    public IList<ScriptDefinition> Scripts { get; init; } = [];
}

public sealed class RunnerOptions
{
    /// <summary>Maximum number of scripts executing at the same time across all users.</summary>
    public int MaxConcurrentRuns { get; set; } = 10;

    /// <summary>Default time limit for a run when the script doesn't set one.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Output records kept per run; further output is dropped with a notice.</summary>
    public int MaxOutputRecords { get; set; } = 50_000;

    /// <summary>Runs one user can have queued or running at once.</summary>
    public int MaxActiveRunsPerUser { get; set; } = 5;

    /// <summary>Runs one user can start per minute.</summary>
    public int MaxRunsPerMinutePerUser { get; set; } = 20;

    /// <summary>Finished runs kept in memory in total; the oldest are dropped first.</summary>
    public int MaxRetainedRuns { get; set; } = 500;

    /// <summary>
    /// Output records kept in memory across all finished runs (roughly 250 bytes each, so the default is about
    /// 125 MB at most); the oldest finished runs are dropped first.
    /// </summary>
    public int MaxRetainedOutputRecords { get; set; } = 500_000;

    /// <summary>How long finished runs stay available in memory.</summary>
    public TimeSpan CompletedRunRetention { get; set; } = TimeSpan.FromHours(4);

    /// <summary>How long run history (status, parameters, output, results) is kept in the database.</summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Objects kept per run for the Results view and CSV/JSON export.</summary>
    public int MaxResultRows { get; set; } = 10_000;

    /// <summary>Console width used when formatting objects (tables, lists) to text.</summary>
    public int OutputWidth { get; set; } = 160;

    /// <summary>
    /// Execution policy for runs that don't use a runspace profile (Windows only; ignored elsewhere).
    /// Null keeps the machine's policy.
    /// </summary>
    public string? ExecutionPolicy { get; set; } = "RemoteSigned";
}
