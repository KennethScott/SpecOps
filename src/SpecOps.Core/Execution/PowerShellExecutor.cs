using System.Management.Automation;
using Microsoft.Extensions.Logging;

namespace SpecOps.Core.Execution;

/// <summary>Where a run's progress goes: the <see cref="ScriptRun"/> itself, or (in the worker) the pipe back to the web app.</summary>
internal interface IRunReporter
{
    CancellationToken CancellationToken { get; }

    bool CancelRequested { get; }

    int ErrorCount { get; }

    int ResultCount { get; }

    void MarkRunning();

    void Append(OutputKind stream, string? text);

    void AddResult(ResultRow row);

    void MarkResultsTruncated();

    void Complete(RunStatus status);
}

/// <summary>The runner settings a run needs, resolved when it starts, so the worker needs no configuration of its own.</summary>
internal sealed record ExecutionSettings(TimeSpan Timeout, string? ExecutionPolicy, int OutputWidth, int MaxResultRows);

/// <summary>Runs a script and reports its progress. Must always bring the run to a final status, and never throw.</summary>
internal interface IScriptExecutor : IDisposable
{
    Task ExecuteAsync(Guid runId, ScriptRunRequest request, ExecutionSettings settings, IRunReporter run);
}

/// <summary>Runs scripts with the PowerShell SDK in this process (the script worker, or tests).</summary>
internal sealed partial class PowerShellExecutor : IScriptExecutor
{
    private readonly RunspacePoolCache _pools = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public PowerShellExecutor(ILogger logger, TimeProvider time)
    {
        _logger = logger;
        _time = time;
    }

    public async Task ExecuteAsync(Guid runId, ScriptRunRequest request, ExecutionSettings settings, IRunReporter run)
    {
        using var timeoutCts = new CancellationTokenSource(settings.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken, timeoutCts.Token);

        var started = _time.GetTimestamp();
        run.MarkRunning();
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
                (pool, rented) = await RentAsync(profile, settings.ExecutionPolicy, linked.Token).ConfigureAwait(false);
                ps = PowerShell.Create();
                ps.Runspace = rented.Runspace;
            }
            else
            {
                ps = PowerShell.Create(RunspacePoolCache.CreateSessionState(settings.ExecutionPolicy, []));
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
                        runId,
                        request.ScriptId,
                        request.ScriptName,
                        request.UserName,
                        request.ClientIp
                    )
                )
                .AddParameter("__SpecOpsScript", request.ScriptPath ?? request.Code)
                .AddParameter("__SpecOpsIsFile", request.ScriptPath is not null)
                .AddParameter("__SpecOpsParameters", parameters)
                .AddParameter("__SpecOpsResults", new ResultCapture(run, settings.MaxResultRows))
                .AddCommand("Out-String")
                .AddParameter("Stream")
                .AddParameter("Width", settings.OutputWidth);

            using var input = new PSDataCollection<PSObject>();
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
                linked.IsCancellationRequested ? CancelledStatus(timeoutCts, run, settings.Timeout)
                : ps.HadErrors || run.ErrorCount > 0 ? RunStatus.CompletedWithErrors
                : RunStatus.Succeeded;
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            // Stopping a pipeline surfaces as different exception types depending on timing
            // (PipelineStoppedException, OperationCanceledException, PSInvalidOperationException...).
            status = CancelledStatus(timeoutCts, run, settings.Timeout);
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
            LogRunCrashed(ex, runId);
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

        run.Append(OutputKind.System, $"Finished: {status} in {_time.GetElapsedTime(started):hh\\:mm\\:ss\\.f}.");
        run.Complete(status);
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

    private static RunStatus CancelledStatus(CancellationTokenSource timeoutCts, IRunReporter run, TimeSpan timeout)
    {
        if (timeoutCts.IsCancellationRequested && !run.CancelRequested)
        {
            run.Append(OutputKind.Error, $"Timed out after {timeout}.");
            return RunStatus.TimedOut;
        }

        run.Append(OutputKind.System, "Cancelled.");
        return RunStatus.Cancelled;
    }

    private static void AttachStreams(PowerShell ps, IRunReporter run)
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

    /// <summary>Rents from the profile's pool; if that pool was just retired by a config change, uses the new one.</summary>
    private async Task<(WarmRunspacePool Pool, PooledRunspace Runspace)> RentAsync(
        Scripts.RunspaceProfile profile,
        string? executionPolicy,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            var pool = _pools.Get(profile, executionPolicy);
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

    public void Dispose() => _pools.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping a pipeline failed")]
    private partial void LogStopFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}: unexpected failure")]
    private partial void LogRunCrashed(Exception ex, Guid runId);
}
