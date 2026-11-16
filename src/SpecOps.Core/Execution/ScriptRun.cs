using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SpecOps.Core.Execution;

public enum OutputKind
{
    Output,
    Error,
    Warning,
    Information,
    Verbose,
    Debug,
    Progress,
    System,
}

public enum RunStatus
{
    Queued,
    Running,
    Succeeded,
    CompletedWithErrors,
    Failed,
    Cancelled,
    TimedOut,
}

public sealed record OutputRecord(long Sequence, DateTimeOffset Timestamp, OutputKind Stream, string Text);

/// <summary>
/// Variables exposed to every script as <c>$SpecOps</c> (e.g. <c>$SpecOps.User</c>), instead of injected parameters,
/// so scripts with strict param() blocks / [CmdletBinding()] still work.
/// </summary>
public sealed record SpecOpsRunContext(Guid RunId, string ScriptId, string ScriptName, string User, string? ClientIp)
{
    public const string VariableName = "SpecOps";
}

/// <summary>
/// A script execution: status plus its output, which any number of viewers can watch live
/// (replaying what they missed), so a page refresh or reconnect doesn't lose anything.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is disposed in Complete(), which every run reaches (the runner guarantees a final status)."
)]
public sealed class ScriptRun
{
    private readonly Lock _lock = new();
    private readonly List<OutputRecord> _records = [];
    private readonly List<Channel<OutputRecord>> _watchers = [];
    private readonly int _maxRecords;
    private readonly TimeProvider _time;
    private long _sequence;
    private bool _truncated;

    internal ScriptRun(ScriptRunRequest request, int maxRecords, TimeProvider time)
    {
        Id = Guid.NewGuid();
        ScriptId = request.ScriptId;
        ScriptName = request.ScriptName;
        UserName = request.UserName;
        UserId = request.UserId;
        Parameters = request.DisplayParameters();
        Code = request.Code;
        _maxRecords = maxRecords;
        _time = time;
        QueuedAt = time.GetUtcNow();
    }

    public Guid Id { get; }

    public string ScriptId { get; }

    public string ScriptName { get; }

    public string UserName { get; }

    public string? UserId { get; }

    /// <summary>Parameter values as text, with sensitive values masked. Safe to display and log.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The ad-hoc code for Terminal runs (null for script runs).</summary>
    public string? Code { get; }

    public DateTimeOffset QueuedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public RunStatus Status { get; private set; } = RunStatus.Queued;

    public int ErrorCount { get; private set; }

    public int WarningCount { get; private set; }

    public bool IsFinished => EndedAt is not null;

    public TimeSpan? Duration => StartedAt is { } start ? (EndedAt ?? _time.GetUtcNow()) - start : null;

    /// <summary>Raised (outside the lock) when the status changes.</summary>
    public event Action<ScriptRun>? StatusChanged;

    // Disposed when the run completes; only touched through TryCancel/CancellationToken while the run is active.
    private readonly CancellationTokenSource _cancellation = new();
    private volatile bool _cancelRequested;

    internal CancellationToken CancellationToken => _cancellation.Token;

    public bool CancelRequested => _cancelRequested;

    /// <summary>Requests cancellation. Returns false if the run already finished.</summary>
    internal bool TryCancel()
    {
        if (IsFinished)
        {
            return false;
        }

        try
        {
            _cancelRequested = true;
            _cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Finished between the check and the cancel.
            return false;
        }
    }

    public IReadOnlyList<OutputRecord> Snapshot()
    {
        lock (_lock)
        {
            return [.. _records];
        }
    }

    /// <summary>Everything written so far, then live output until the run finishes or <paramref name="cancellationToken"/> fires.</summary>
    public async IAsyncEnumerable<OutputRecord> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        OutputRecord[] backlog;
        Channel<OutputRecord>? channel = null;
        lock (_lock)
        {
            backlog = [.. _records];
            if (!IsFinished)
            {
                channel = Channel.CreateUnbounded<OutputRecord>(new UnboundedChannelOptions { SingleReader = true });
                _watchers.Add(channel);
            }
        }

        try
        {
            foreach (var record in backlog)
            {
                yield return record;
            }

            if (channel is null)
            {
                yield break;
            }

            await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }
        finally
        {
            if (channel is not null)
            {
                lock (_lock)
                {
                    _watchers.Remove(channel);
                }
            }
        }
    }

    internal void Append(OutputKind stream, string? text)
    {
        text ??= "";
        lock (_lock)
        {
            if (stream == OutputKind.Error)
            {
                ErrorCount++;
            }
            else if (stream == OutputKind.Warning)
            {
                WarningCount++;
            }

            if (_records.Count >= _maxRecords)
            {
                if (_truncated)
                {
                    return;
                }

                _truncated = true;
                stream = OutputKind.System;
                text = $"Output limit of {_maxRecords:N0} records reached; further output is not shown.";
            }

            var record = new OutputRecord(++_sequence, _time.GetUtcNow(), stream, text);
            _records.Add(record);
            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryWrite(record);
            }
        }
    }

    internal void MarkRunning()
    {
        lock (_lock)
        {
            StartedAt = _time.GetUtcNow();
            Status = RunStatus.Running;
        }

        StatusChanged?.Invoke(this);
    }

    internal void Complete(RunStatus status)
    {
        lock (_lock)
        {
            if (IsFinished)
            {
                return;
            }

            StartedAt ??= _time.GetUtcNow();
            EndedAt = _time.GetUtcNow();
            Status = status;
            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryComplete();
            }

            _watchers.Clear();
        }

        _cancellation.Dispose();
        StatusChanged?.Invoke(this);
    }
}
