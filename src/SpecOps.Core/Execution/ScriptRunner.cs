using System.Collections.Concurrent;
using System.Management.Automation;
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
    private readonly RunspacePoolCache _pools = new();
    private readonly RunGate _gate;
    private readonly IDisposable? _optionsChange;
    private readonly IOptionsMonitor<SpecOpsOptions> _options;
    private readonly ILogger<ScriptRunner> _logger;
    private readonly TimeProvider _time;
    private volatile bool _stopping;

    public ScriptRunner(
        IOptionsMonitor<SpecOpsOptions> options,
        ILogger<ScriptRunner> logger,
        TimeProvider? time = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
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
                await ExecuteAsync(run, request).ConfigureAwait(false);
            }
            finally
            {
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
        var timeout = request.Timeout ?? RunnerOptions.DefaultTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken, timeoutCts.Token);

        run.MarkRunning();
        LogRunStarted(run.Id, run.UserName, run.ScriptId, run.Parameters, request.ClientIp);
        if (request.Code is not null)
        {
            // Ad-hoc code is the most powerful thing SpecOps can run: always keep a record of exactly what ran.
            LogCodeRun(run.Id, run.UserName, request.ClientIp, request.Code);
        }

        run.Append(OutputKind.System, "Starting...");

        RunStatus status;
        WarmRunspacePool? pool = null;
        PooledRunspace? rented = null;
        try
        {
            if (request.ScriptPath is not null && !File.Exists(request.ScriptPath))
            {
                throw new FileNotFoundException($"Script file not found: {request.ScriptPath}");
            }

            PowerShell ps;
            if (request.RunspaceProfile is { } profile)
            {
                (pool, rented) = await RentAsync(profile, linked.Token).ConfigureAwait(false);
                ps = PowerShell.Create();
                ps.Runspace = rented.Runspace;
            }
            else
            {
                ps = PowerShell.Create(RunspacePoolCache.CreateSessionState(RunnerOptions.ExecutionPolicy, []));
            }

            using var _ = ps;
            AttachStreams(ps, run);

            var parameters = new System.Collections.Hashtable(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in request.Parameters)
            {
                parameters[parameter.Key] = parameter.Value;
            }

            // One pipeline: wrapper (sets $SpecOps, invokes the script with splatted parameters) | Out-String.
            // Out-String formats objects (tables, lists) the same way the console would, streaming line by line.
            ps.AddScript(WrapperScript, useLocalScope: true)
                .AddParameter(
                    "__SpecOpsContext",
                    new SpecOpsRunContext(
                        run.Id,
                        request.ScriptId,
                        request.ScriptName,
                        request.UserName,
                        request.ClientIp
                    )
                )
                .AddParameter("__SpecOpsScript", request.ScriptPath ?? request.Code)
                .AddParameter("__SpecOpsIsFile", request.ScriptPath is not null)
                .AddParameter("__SpecOpsParameters", parameters)
                .AddParameter("__SpecOpsResults", new ResultCapture(run, RunnerOptions.MaxResultRows))
                .AddCommand("Out-String")
                .AddParameter("Stream")
                .AddParameter("Width", RunnerOptions.OutputWidth);

            var input = new PSDataCollection<PSObject>();
            input.Complete();
            using var output = new PSDataCollection<PSObject>();
            output.DataAdded += (_, e) => run.Append(OutputKind.Output, output[e.Index]?.BaseObject?.ToString());

            // Cancelled while we were still setting up: don't start the pipeline at all.
            linked.Token.ThrowIfCancellationRequested();
            using (linked.Token.Register(() => StopPipeline(ps)))
            {
                await ps.InvokeAsync(input, output).ConfigureAwait(false);
            }

            status =
                linked.IsCancellationRequested ? CancelledStatus(timeoutCts, run, timeout)
                : ps.HadErrors || run.ErrorCount > 0 ? RunStatus.CompletedWithErrors
                : RunStatus.Succeeded;
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            // Stopping a pipeline surfaces as different exception types depending on timing
            // (PipelineStoppedException, OperationCanceledException, PSInvalidOperationException...).
            status = CancelledStatus(timeoutCts, run, timeout);
        }
        catch (Exception ex) when (ex is RuntimeException or IOException or UnauthorizedAccessException)
        {
            var message = ex is RuntimeException { ErrorRecord: { } record } ? record.ToString() : ex.Message;
            run.Append(OutputKind.Error, message);
            status = RunStatus.Failed;
        }
#pragma warning disable CA1031 // A run must always reach a final status; anything unexpected is reported as a failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRunCrashed(ex, run.Id);
            run.Append(OutputKind.Error, $"Unexpected error: {ex.Message}");
            status = RunStatus.Failed;
        }
        finally
        {
            if (rented is not null)
            {
                pool!.Return(rented);
            }
        }

        run.Append(OutputKind.System, $"Finished: {status} in {run.Duration:hh\\:mm\\:ss\\.f}.");
        run.Complete(status);
        LogRunFinished(run.Id, run.ScriptId, status, run.ErrorCount);
    }

    /// <summary>
    /// Runs the user's script (a file, so $PSScriptRoot works, or inline code) with splatted parameters,
    /// after exposing the run context as $SpecOps. Every emitted object is handed to the result collector and then
    /// passed on unchanged, so the console text is exactly what it would be otherwise. Terminating errors propagate
    /// and fail the run.
    /// </summary>
    private const string WrapperScript = """
        param($__SpecOpsContext, [string]$__SpecOpsScript, [bool]$__SpecOpsIsFile, [hashtable]$__SpecOpsParameters, $__SpecOpsResults)
        Set-Variable -Name SpecOps -Value $__SpecOpsContext -Scope Global -Force
        $__SpecOpsCommand = if ($__SpecOpsIsFile) { $__SpecOpsScript } else { [scriptblock]::Create($__SpecOpsScript) }
        & $__SpecOpsCommand @__SpecOpsParameters | ForEach-Object { $__SpecOpsResults.Add($_); $_ }
        """;

    private static RunStatus CancelledStatus(CancellationTokenSource timeoutCts, ScriptRun run, TimeSpan timeout)
    {
        if (timeoutCts.IsCancellationRequested && !run.CancelRequested)
        {
            run.Append(OutputKind.Error, $"Timed out after {timeout}.");
            return RunStatus.TimedOut;
        }

        run.Append(OutputKind.System, "Cancelled.");
        return RunStatus.Cancelled;
    }

    private static void AttachStreams(PowerShell ps, ScriptRun run)
    {
        ps.Streams.Error.DataAdded += (s, e) => run.Append(OutputKind.Error, ps.Streams.Error[e.Index].ToString());
        ps.Streams.Warning.DataAdded += (s, e) => run.Append(OutputKind.Warning, ps.Streams.Warning[e.Index].Message);
        ps.Streams.Information.DataAdded += (s, e) =>
            run.Append(OutputKind.Information, ps.Streams.Information[e.Index].MessageData?.ToString());
        ps.Streams.Verbose.DataAdded += (s, e) => run.Append(OutputKind.Verbose, ps.Streams.Verbose[e.Index].Message);
        ps.Streams.Debug.DataAdded += (s, e) => run.Append(OutputKind.Debug, ps.Streams.Debug[e.Index].Message);
        ps.Streams.Progress.DataAdded += (s, e) =>
        {
            var p = ps.Streams.Progress[e.Index];
            if (p.RecordType != ProgressRecordType.Completed)
            {
                run.Append(
                    OutputKind.Progress,
                    p.PercentComplete >= 0
                        ? $"{p.Activity}: {p.StatusDescription} ({p.PercentComplete}%)"
                        : $"{p.Activity}: {p.StatusDescription}"
                );
            }
        };
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

    /// <summary>Rents from the profile's pool; if that pool was just retired by a config change, uses the new one.</summary>
    private async Task<(WarmRunspacePool Pool, PooledRunspace Runspace)> RentAsync(
        Scripts.RunspaceProfile profile,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            var pool = _pools.Get(profile, RunnerOptions.ExecutionPolicy);
            try
            {
                return (pool, await pool.RentAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (ObjectDisposedException) when (attempt < 3)
            {
                // Retired between lookup and rent; the next lookup returns the replacement.
            }
        }
    }

    /// <summary>Asks PowerShell to stop the pipeline without blocking the caller (often a UI thread).</summary>
    private void StopPipeline(PowerShell ps) =>
        _ = ps.StopAsync(null, null)
            .ContinueWith(
                t => LogStopFailed(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default
            );

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
        _pools.Dispose();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping a pipeline failed")]
    private partial void LogStopFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}: unexpected failure")]
    private partial void LogRunCrashed(Exception ex, Guid runId);
}
