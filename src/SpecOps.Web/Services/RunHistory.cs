using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;
using SpecOps.Core.Execution;
using SpecOps.Data;

namespace SpecOps.Web.Services;

/// <summary>One row of the Runs list (no output or results).</summary>
public sealed record RunSummary(
    Guid Id,
    string ScriptId,
    string ScriptName,
    string UserName,
    DateTimeOffset QueuedAt,
    TimeSpan? Duration,
    RunStatus Status
)
{
    public bool IsFinished => Status is not (RunStatus.Queued or RunStatus.Running);

    public static RunSummary From(ScriptRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new(run.Id, run.ScriptId, run.ScriptName, run.UserName, run.QueuedAt, run.Duration, run.Status);
    }
}

/// <summary>
/// Run history in the database: every run is stored when it's queued and again when it finishes, with its output
/// and results, so it outlives the runner's in-memory window and restarts. Writing never throws (a failed write is
/// logged and the run carries on), the same as the audit log.
/// </summary>
public sealed partial class RunHistory(
    IServiceScopeFactory scopes,
    IOptionsMonitor<SpecOpsOptions> options,
    TimeProvider time,
    ILogger<RunHistory> logger
) : IRunHistory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task RunStartedAsync(ScriptRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return WriteAsync(run.Id, run.ToSnapshot(), includeOutput: false);
    }

    public Task RunFinishedAsync(ScriptRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return WriteAsync(run.Id, run.ToSnapshot(), includeOutput: true);
    }

    /// <summary>A stored run with its output and results, or null.</summary>
    public async Task<RunSnapshot?> FindAsync(Guid runId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        var record = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
        return record is null ? null : ToSnapshot(record);
    }

    /// <summary>The most recent runs, newest first; only <paramref name="userId"/>'s when it's set.</summary>
    public async Task<IReadOnlyList<RunSummary>> ListAsync(string? userId, int take)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        var query = db.Runs.AsNoTracking();
        if (userId is not null)
        {
            query = query.Where(r => r.UserId == userId);
        }

        var rows = await query
            .OrderByDescending(r => r.QueuedAtUtc)
            .Take(take)
            .Select(r => new
            {
                r.Id,
                r.ScriptId,
                r.ScriptName,
                r.UserName,
                r.QueuedAtUtc,
                r.StartedAtUtc,
                r.EndedAtUtc,
                r.Status,
            })
            .ToListAsync();

        var now = time.GetUtcNow();
        return
        [
            .. rows.Select(r => new RunSummary(
                r.Id,
                r.ScriptId,
                r.ScriptName,
                r.UserName,
                Utc(r.QueuedAtUtc),
                r.StartedAtUtc is { } started ? (r.EndedAtUtc is { } ended ? Utc(ended) : now) - Utc(started) : null,
                ParseStatus(r.Status)
            )),
        ];
    }

    /// <summary>
    /// At startup, before any run starts: runs still marked queued or running were cut off by a restart or crash.
    /// Returns how many were marked <see cref="RunStatus.Interrupted"/>.
    /// </summary>
    public async Task<int> MarkInterruptedAsync()
    {
        var output = JsonSerializer.Serialize<IReadOnlyList<OutputRecord>>(
            [
                new OutputRecord(
                    1,
                    time.GetUtcNow(),
                    OutputKind.System,
                    "SpecOps stopped while this run was in progress, so its output wasn't saved."
                ),
            ],
            Json
        );

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        return await db
            .Runs.Where(r => r.EndedAtUtc == null)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(r => r.Status, nameof(RunStatus.Interrupted))
                    .SetProperty(r => r.EndedAtUtc, r => r.StartedAtUtc ?? r.QueuedAtUtc)
                    .SetProperty(r => r.OutputJson, output)
            );
    }

    /// <summary>Deletes finished runs older than Runner.HistoryRetention. Returns how many were deleted.</summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = (time.GetUtcNow() - options.CurrentValue.Runner.HistoryRetention).UtcDateTime;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        return await db
            .Runs.Where(r => r.EndedAtUtc != null && r.QueuedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task WriteAsync(Guid runId, RunSnapshot snapshot, bool includeOutput)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
            // The start write may have failed, so the finish write inserts if needed.
            var record = await db.Runs.FindAsync(runId);
            if (record is null)
            {
                record = new RunRecord { Id = runId };
                db.Runs.Add(record);
            }

            Apply(record, snapshot, includeOutput);
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException)
        {
            LogWriteFailed(ex, runId);
        }
    }

    private static void Apply(RunRecord record, RunSnapshot run, bool includeOutput)
    {
        record.ScriptId = Truncate(run.ScriptId, 128);
        record.ScriptName = Truncate(run.ScriptName, 256);
        record.UserId = run.UserId;
        record.UserName = Truncate(run.UserName, 256);
        record.ClientIp = run.ClientIp is null ? null : Truncate(run.ClientIp, 64);
        record.ParametersJson = JsonSerializer.Serialize(run.Parameters, Json);
        record.Code = run.Code;
        record.QueuedAtUtc = run.QueuedAt.UtcDateTime;
        record.StartedAtUtc = run.StartedAt?.UtcDateTime;
        record.EndedAtUtc = run.EndedAt?.UtcDateTime;
        record.Status = run.Status.ToString();
        record.ErrorCount = run.ErrorCount;
        record.WarningCount = run.WarningCount;
        record.ResultCount = run.Results.Count;
        record.ResultsTruncated = run.ResultsTruncated;
        if (includeOutput)
        {
            record.OutputJson = JsonSerializer.Serialize(run.Output, Json);
            record.ResultsJson = run.Results.Count > 0 ? JsonSerializer.Serialize(run.Results, Json) : null;
        }
    }

    private static RunSnapshot ToSnapshot(RunRecord record) =>
        new()
        {
            Id = record.Id,
            ScriptId = record.ScriptId,
            ScriptName = record.ScriptName,
            UserName = record.UserName,
            UserId = record.UserId,
            ClientIp = record.ClientIp,
            Parameters = new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(record.ParametersJson, Json)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase
            ),
            Code = record.Code,
            QueuedAt = Utc(record.QueuedAtUtc),
            StartedAt = record.StartedAtUtc is { } started ? Utc(started) : null,
            EndedAt = record.EndedAtUtc is { } ended ? Utc(ended) : null,
            Status = ParseStatus(record.Status),
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            ResultsTruncated = record.ResultsTruncated,
            Output = record.OutputJson is null
                ? []
                : JsonSerializer.Deserialize<List<OutputRecord>>(record.OutputJson, Json) ?? [],
            Results = record.ResultsJson is null
                ? []
                : JsonSerializer.Deserialize<List<ResultRow>>(record.ResultsJson, Json) ?? [],
        };

    // SQLite hands DateTime back as Unspecified; it's always stored as UTC.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static RunStatus ParseStatus(string status) =>
        Enum.TryParse<RunStatus>(status, out var parsed) ? parsed : RunStatus.Failed;

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save run {RunId} to run history")]
    private partial void LogWriteFailed(Exception ex, Guid runId);
}
