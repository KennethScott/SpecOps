using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SpecOps.Core.Execution;

/// <summary>How to start the script worker (the web app runs itself with a worker argument). The pipe name is appended.</summary>
public sealed record ScriptWorkerCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Runs scripts in the script worker process (see <see cref="ScriptWorker"/>), started on first use and again after it
/// stops. If it dies, its active runs fail; the web app and other runs carry on.
/// </summary>
internal sealed partial class WorkerExecutor : IScriptExecutor
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ScriptWorkerCommand _command;
    private readonly ILogger _logger;
    private WorkerProcess? _worker;
    private bool _disposed;

    public WorkerExecutor(ScriptWorkerCommand command, ILogger logger)
    {
        _command = command;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid runId, ScriptRunRequest request, ExecutionSettings settings, IRunReporter run)
    {
        WorkerProcess worker;
        try
        {
            worker = await GetWorkerAsync(run.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested)
        {
            run.Append(OutputKind.System, "Cancelled before it started.");
            run.Complete(RunStatus.Cancelled);
            return;
        }
#pragma warning disable CA1031 // A run must always reach a final status.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStartFailed(_logger, ex);
            run.Append(OutputKind.Error, $"Couldn't start the script worker: {ex.Message}");
            run.Complete(RunStatus.Failed);
            return;
        }

        await worker.RunAsync(runId, request, settings, run).ConfigureAwait(false);
    }

    private async Task<WorkerProcess> GetWorkerAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsAlive: true } alive)
            {
                return alive;
            }

            _worker?.Dispose();
            _worker = null;
            _worker = await WorkerProcess.StartAsync(_command, _logger, cancellationToken).ConfigureAwait(false);
            return _worker;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Wait();
        try
        {
            _disposed = true;
            _worker?.Dispose();
            _worker = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't start the script worker")]
    private static partial void LogStartFailed(ILogger logger, Exception ex);
}

/// <summary>One script worker process and its pipe: sends runs to it and routes their progress back.</summary>
internal sealed partial class WorkerProcess : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly WorkerConnection _connection;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (IRunReporter Run, TaskCompletionSource Done)> _runs = [];
    private readonly Task _reading;
    private bool _stopped;

    private WorkerProcess(Process process, NamedPipeServerStream pipe, ILogger logger)
    {
        _process = process;
        _logger = logger;
        _connection = new WorkerConnection(pipe);
        _reading = Task.Run(ReadAsync);
    }

    public bool IsAlive
    {
        get
        {
            lock (_lock)
            {
                return !_stopped;
            }
        }
    }

    public static async Task<WorkerProcess> StartAsync(
        ScriptWorkerCommand command,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var pipeName = $"specops-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(pipeName);
        var process = new Process { StartInfo = start };
        // Anything written straight to the console (the worker's own log, or [Console]::WriteLine in a script).
        process.OutputDataReceived += (_, e) => LogOutput(logger, e.Data);
        process.ErrorDataReceived += (_, e) => LogOutput(logger, e.Data);
        try
        {
            process.Start();
            // Nothing to read: a script waiting for console input gets end-of-input rather than hanging.
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartTimeout);
            var connecting = pipe.WaitForConnectionAsync(timeout.Token);
            if (
                await Task.WhenAny(connecting, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false)
                != connecting
            )
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The script worker exited with code {process.ExitCode} before it was ready."
                    );
                }
            }

            try
            {
                await connecting.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The script worker didn't start within {StartTimeout.TotalSeconds} seconds.",
                    ex
                );
            }

            LogStarted(logger, process.Id);
            return new WorkerProcess(process, pipe, logger);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>Runs one script in the worker; returns once the run has its final status.</summary>
    public async Task RunAsync(Guid runId, ScriptRunRequest request, ExecutionSettings settings, IRunReporter run)
    {
        StartRun start;
        try
        {
            start = new StartRun(runId, WorkerRequest.From(request), settings);
        }
        catch (NotSupportedException ex)
        {
            run.Append(OutputKind.Error, ex.Message);
            run.Complete(RunStatus.Failed);
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool accepted;
        lock (_lock)
        {
            accepted = !_stopped;
            if (accepted)
            {
                _runs[runId] = (run, done);
            }
        }

        if (!accepted)
        {
            // The worker stopped just now; the next run starts a new one.
            run.Append(OutputKind.Error, "The script worker stopped before the run could start; try again.");
            run.Complete(RunStatus.Failed);
            return;
        }

        _connection.Send(start);
        using (run.CancellationToken.Register(() => _connection.Send(new CancelRun(runId))))
        {
            await done.Task.ConfigureAwait(false);
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var message in _connection.ReadAllAsync().ConfigureAwait(false))
            {
                Dispatch(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The pipe closed: the worker exited, or we're shutting down.
        }
        catch (JsonException ex)
        {
            LogBadMessage(_logger, ex);
        }
        finally
        {
            Stopped();
        }
    }

    private void Dispatch(WorkerMessage message)
    {
        IRunReporter run;
        TaskCompletionSource done;
        lock (_lock)
        {
            if (!_runs.TryGetValue(message.RunId, out var entry))
            {
                return;
            }

            (run, done) = entry;
            if (message is RunFinished)
            {
                _runs.Remove(message.RunId);
            }
        }

        switch (message)
        {
            case RunStarted:
                run.MarkRunning();
                break;
            case RunOutput output:
                run.Append(output.Stream, output.Text);
                break;
            case RunResult result:
                run.AddResult(result.Row);
                break;
            case RunResultsTruncated:
                run.MarkResultsTruncated();
                break;
            case RunFinished finished:
                run.Complete(finished.Status);
                done.TrySetResult();
                break;
        }
    }

    /// <summary>The worker has gone: fail whatever it was still running.</summary>
    private void Stopped()
    {
        List<(IRunReporter Run, TaskCompletionSource Done)> orphans;
        lock (_lock)
        {
            _stopped = true;
            orphans = [.. _runs.Values];
            _runs.Clear();
        }

        if (orphans.Count == 0)
        {
            return;
        }

        var exitCode = _process.WaitForExit(TimeSpan.FromSeconds(5)) ? $" (exit code {_process.ExitCode})" : "";
        LogStoppedWithRuns(_logger, orphans.Count, exitCode);
        foreach (var (run, done) in orphans)
        {
            run.Append(
                OutputKind.Error,
                $"The script worker stopped unexpectedly{exitCode}, so this run didn't finish."
            );
            run.Complete(RunStatus.Failed);
            done.TrySetResult();
        }
    }

    /// <summary>Closes the pipe, which tells the worker to exit; kills it if it doesn't.</summary>
    public void Dispose()
    {
        _connection.Dispose();
        if (!_process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            Kill(_process);
        }

        _reading.Wait(TimeSpan.FromSeconds(5));
        _process.Dispose();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Never started, or already gone.
        }
    }

    private static void LogOutput(ILogger logger, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            LogWorkerOutput(logger, line);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Script worker started (process {ProcessId})")]
    private static partial void LogStarted(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Script worker: {Line}")]
    private static partial void LogWorkerOutput(ILogger logger, string line);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The script worker stopped unexpectedly{ExitCode}; {Count} runs failed"
    )]
    private static partial void LogStoppedWithRuns(ILogger logger, int count, string exitCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "The script worker sent a message that couldn't be read")]
    private static partial void LogBadMessage(ILogger logger, Exception ex);
}
