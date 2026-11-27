using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;

namespace SpecOps.Core.Execution;

/// <summary>A run was refused before it started (per-user limits). The message is safe to show to the user.</summary>
public sealed class RunRejectedException : Exception
{
    public RunRejectedException() { }

    public RunRejectedException(string message)
        : base(message) { }

    public RunRejectedException(string message, Exception innerException)
        : base(message, innerException) { }
}

public interface IScriptRunner
{
    /// <summary>Queues the run and returns immediately; watch progress via <see cref="ScriptRun.WatchAsync"/>.</summary>
    /// <exception cref="RunRejectedException">The user has too many active runs or started too many recently.</exception>
    ScriptRun Start(ScriptRunRequest request);

    ScriptRun? Find(Guid runId);

    /// <summary>Active runs and recently finished ones (kept in memory for <see cref="RunnerOptions.CompletedRunRetention"/>).</summary>
    IReadOnlyList<ScriptRun> Runs { get; }

    /// <summary>Stops the pipeline (not just the waiting). Returns false if the run is unknown or already finished.</summary>
    bool Cancel(Guid runId);

    /// <summary>Graceful shutdown: refuses new runs, cancels active and queued ones, and waits for them to finish.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed partial class ScriptRunner : IScriptRunner, IDisposable
{
    private readonly ConcurrentDictionary<Guid, ScriptRun> _runs = new();
    private readonly ConcurrentDictionary<Guid, Task> _executions = new();
    private readonly Lock _startLock = new();
    private readonly IScriptExecutor _executor;
    private readonly RunGate _gate;
    private readonly IDisposable? _optionsChange;
    private readonly IOptionsMonitor<SpecOpsOptions> _options;
    private readonly ILogger<ScriptRunner> _logger;
    private readonly TimeProvider _time;
    private readonly IRunHistory? _history;
    private volatile bool _stopping;

    public ScriptRunner(
        IOptionsMonitor<SpecOpsOptions> options,
        ILogger<ScriptRunner> logger,
        TimeProvider? time = null,
        IRunHistory? history = null,
        ScriptWorkerCommand? worker = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _history = history;
        // In the web app scripts run in a separate worker process; tests and other hosts can run them right here.
        _executor = worker is null ? new PowerShellExecutor(logger, _time) : new WorkerExecutor(worker, logger);
        _gate = new RunGate(() => RunnerOptions.MaxConcurrentRuns);
        // A raised MaxConcurrentRuns admits queued runs straight away.
        _optionsChange = options.OnChange((_, _) => _gate.Pump());
    }

    private RunnerOptions RunnerOptions => _options.CurrentValue.Runner;

    public IReadOnlyList<ScriptRun> Runs => [.. _runs.Values.OrderByDescending(r => r.QueuedAt)];

    public ScriptRun? Find(Guid runId) => _runs.GetValueOrDefault(runId);

    public ScriptRun Start(ScriptRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.ScriptPath) == string.IsNullOrEmpty(request.Code))
        {
            throw new ArgumentException("Exactly one of ScriptPath or Code must be set.", nameof(request));
        }

        ScriptRun run;
        lock (_startLock)
        {
            if (_stopping)
            {
                throw new RunRejectedException("SpecOps is shutting down; try again shortly.");
            }

            PruneFinishedRuns();
            EnforceUserLimits(request);
            run = new ScriptRun(request, RunnerOptions.MaxOutputRecords, _time);
            _runs[run.Id] = run;
        }

        _executions[run.Id] = Task.Run(async () =>
        {
            try
            {
                if (_history is not null)
                {
                    await _history.RunStartedAsync(run).ConfigureAwait(false);
                }

                await ExecuteAsync(run, request).ConfigureAwait(false);
            }
            finally
            {
                // Saved before the execution is forgotten, so a graceful shutdown (StopAsync) waits for it.
                if (_history is not null)
                {
                    await _history.RunFinishedAsync(run).ConfigureAwait(false);
                }

                _executions.TryRemove(run.Id, out _);
            }
        });
        return run;
    }

    private void EnforceUserLimits(ScriptRunRequest request)
    {
        var options = RunnerOptions;
        var user = UserKey(request.UserId, request.UserName);
        var mine = _runs
            .Values.Where(r => string.Equals(UserKey(r.UserId, r.UserName), user, StringComparison.Ordinal))
            .ToList();

        if (mine.Count(r => !r.IsFinished) >= options.MaxActiveRunsPerUser)
        {
            throw new RunRejectedException(
                $"You already have {options.MaxActiveRunsPerUser} runs in progress. Wait for one to finish or cancel one."
            );
        }

        var oneMinuteAgo = _time.GetUtcNow().AddMinutes(-1);
        if (mine.Count(r => r.QueuedAt > oneMinuteAgo) >= options.MaxRunsPerMinutePerUser)
        {
            throw new RunRejectedException("You're starting runs too quickly. Try again in a minute.");
        }
    }

    private static string UserKey(string? userId, string userName) => userId ?? $"name:{userName}";

    public bool Cancel(Guid runId) => _runs.TryGetValue(runId, out var run) && run.TryCancel();

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_startLock)
        {
            _stopping = true;
        }

        foreach (var run in _runs.Values)
        {
            run.TryCancel();
        }

        try
        {
            await Task.WhenAll(_executions.Values).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogShutdownTimedOut(_executions.Count);
        }
    }

    private async Task ExecuteAsync(ScriptRun run, ScriptRunRequest request)
    {
        if (!_gate.HasFreeSlot)
        {
            run.Append(OutputKind.System, "Waiting for a free slot (too many scripts running)...");
        }

        IDisposable slot;
        try
        {
            slot = await _gate.EnterAsync(run.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            run.Append(OutputKind.System, "Cancelled before it started.");
            run.Complete(RunStatus.Cancelled);
            return;
        }

        using (slot)
        {
            await ExecuteCoreAsync(run, request).ConfigureAwait(false);
        }
    }

    private async Task ExecuteCoreAsync(ScriptRun run, ScriptRunRequest request)
    {
        LogRunStarted(run.Id, run.UserName, run.ScriptId, run.Parameters, request.ClientIp);
        if (request.Code is not null)
        {
            // Ad-hoc code is the most powerful thing SpecOps can run: always keep a record of exactly what ran.
            LogCodeRun(run.Id, run.UserName, request.ClientIp, request.Code);
        }

        var options = RunnerOptions;
        var settings = new ExecutionSettings(
            request.Timeout ?? options.DefaultTimeout,
            options.ExecutionPolicy,
            options.OutputWidth,
            options.MaxResultRows
        );
        await _executor.ExecuteAsync(run.Id, request, settings, run).ConfigureAwait(false);
        LogRunFinished(run.Id, run.ScriptId, run.Status, run.ErrorCount);
    }

    /// <summary>Drops finished runs past their retention, then the oldest finished runs beyond the retained-run cap.</summary>
    /// <remarks>
    /// Also enforces a total output budget across finished runs (MaxRetainedOutputRecords), so memory stays bounded
    /// however many long-running, chatty scripts people run. Active runs are never dropped.
    /// </remarks>
    private void PruneFinishedRuns()
    {
        var options = RunnerOptions;
        var cutoff = _time.GetUtcNow() - options.CompletedRunRetention;
        var finished = _runs.Values.Where(r => r.IsFinished).OrderBy(r => r.EndedAt).ToList();
        var excessRuns = Math.Max(0, _runs.Count - options.MaxRetainedRuns + 1);
        var excessRecords = finished.Sum(r => (long)r.RecordCount) - options.MaxRetainedOutputRecords;

        foreach (var run in finished)
        {
            if (run.EndedAt < cutoff || excessRuns > 0 || excessRecords > 0)
            {
                _runs.TryRemove(run.Id, out _);
                excessRuns--;
                excessRecords -= run.RecordCount;
            }
        }
    }

    /// <summary>
    /// Last-resort cleanup. In the web app <see cref="StopAsync"/> runs first (host shutdown); this covers tests
    /// and other hosts: cancel, wait briefly, then release runspaces.
    /// </summary>
    public void Dispose()
    {
        _stopping = true;
        _optionsChange?.Dispose();
        foreach (var run in _runs.Values)
        {
            run.TryCancel();
        }

        Task.WaitAll([.. _executions.Values], TimeSpan.FromSeconds(10));
        _executor.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Run {RunId}: {User} from {ClientIp} started '{ScriptId}' with {Parameters}"
    )]
    private partial void LogRunStarted(
        Guid runId,
        string user,
        string scriptId,
        IReadOnlyDictionary<string, string> parameters,
        string? clientIp
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: {User} from {ClientIp} ran ad-hoc code:\n{Code}")]
    private partial void LogCodeRun(Guid runId, string user, string? clientIp, string code);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Run {RunId}: '{ScriptId}' finished {Status} with {ErrorCount} errors"
    )]
    private partial void LogRunFinished(Guid runId, string scriptId, RunStatus status, int errorCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown: {Count} runs were still finishing when time ran out")]
    private partial void LogShutdownTimedOut(int count);
}
