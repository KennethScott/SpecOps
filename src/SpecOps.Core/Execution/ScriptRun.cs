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

    /// <summary>SpecOps stopped (restart, crash) while the run was in progress; its output was lost.</summary>
    Interrupted,
}

public sealed record OutputRecord(long Sequence, DateTimeOffset Timestamp, OutputKind Stream, string Text);

/// <summary>Everything about a run, as stored in run history. See <see cref="ScriptRun.ToSnapshot"/>.</summary>
public sealed record RunSnapshot
{
    public required Guid Id { get; init; }

    public required string ScriptId { get; init; }

    public required string ScriptName { get; init; }

    public required string UserName { get; init; }

    public string? UserId { get; init; }

    public string? ClientIp { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Code { get; init; }

    public required DateTimeOffset QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public required RunStatus Status { get; init; }

    public int ErrorCount { get; init; }

    public int WarningCount { get; init; }

    public bool ResultsTruncated { get; init; }

    public IReadOnlyList<OutputRecord> Output { get; init; } = [];

    public IReadOnlyList<ResultRow> Results { get; init; } = [];
}

/// <summary>Keeps runs beyond the runner's in-memory window (the web app stores them in the database).</summary>
public interface IRunHistory
{
    /// <summary>Called when a run is queued. Must not throw: a failed write must not stop the run.</summary>
    Task RunStartedAsync(ScriptRun run);

    /// <summary>Called once a run has its final status. Must not throw.</summary>
    Task RunFinishedAsync(ScriptRun run);
}

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
public sealed class ScriptRun : IRunReporter
{
    private readonly Lock _lock = new();
    private readonly List<OutputRecord> _records = [];
    private readonly List<ResultRow> _results = [];
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
        ClientIp = request.ClientIp;
        Parameters = request.DisplayParameters();
        Code = request.Code;
        _maxRecords = maxRecords;
        _time = time;
        QueuedAt = time.GetUtcNow();
    }

    private ScriptRun(RunSnapshot snapshot, TimeProvider time)
    {
        Id = snapshot.Id;
        ScriptId = snapshot.ScriptId;
        ScriptName = snapshot.ScriptName;
        UserName = snapshot.UserName;
        UserId = snapshot.UserId;
        ClientIp = snapshot.ClientIp;
        Parameters = snapshot.Parameters;
        Code = snapshot.Code;
        QueuedAt = snapshot.QueuedAt;
        StartedAt = snapshot.StartedAt;
        EndedAt = snapshot.EndedAt ?? snapshot.StartedAt ?? snapshot.QueuedAt;
        Status = snapshot.Status;
        ErrorCount = snapshot.ErrorCount;
        WarningCount = snapshot.WarningCount;
        ResultsTruncated = snapshot.ResultsTruncated;
        _records.AddRange(snapshot.Output);
        _results.AddRange(snapshot.Results);
        _sequence = snapshot.Output.Count > 0 ? snapshot.Output[^1].Sequence : 0;
        _maxRecords = _records.Count;
        _time = time;
        _cancellation.Dispose();
    }

    /// <summary>A finished, read-only run rebuilt from history, for the same pages that show live runs.</summary>
    public static ScriptRun FromSnapshot(RunSnapshot snapshot, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ScriptRun(snapshot, time ?? TimeProvider.System);
    }

    /// <summary>A consistent copy of the run as it is right now (output and results included).</summary>
    public RunSnapshot ToSnapshot()
    {
        lock (_lock)
        {
            return new RunSnapshot
            {
                Id = Id,
                ScriptId = ScriptId,
                ScriptName = ScriptName,
                UserName = UserName,
                UserId = UserId,
                ClientIp = ClientIp,
                Parameters = Parameters,
                Code = Code,
                QueuedAt = QueuedAt,
                StartedAt = StartedAt,
                EndedAt = EndedAt,
                Status = Status,
                ErrorCount = ErrorCount,
                WarningCount = WarningCount,
                ResultsTruncated = ResultsTruncated,
                Output = [.. _records],
                Results = [.. _results],
            };
        }
    }

    public Guid Id { get; }

    public string ScriptId { get; }

    public string ScriptName { get; }

    public string UserName { get; }

    public string? UserId { get; }

    public string? ClientIp { get; }

    /// <summary>Parameter values as text, with sensitive values masked. Safe to display and log.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The ad-hoc code for Terminal runs (null for script runs).</summary>
    public string? Code { get; }

    public DateTimeOffset QueuedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public RunStatus Status { get; private set; } = RunStatus.Queued;

    public int ErrorCount { get; private set; }

    /// <summary>Output records plus result rows held for this run (used for the memory budget).</summary>
    public int RecordCount
    {
        get
        {
            lock (_lock)
            {
                return _records.Count + _results.Count;
            }
        }
    }

    /// <summary>Objects the script emitted (as rows), for the Results view and CSV/JSON export.</summary>
    public int ResultCount
    {
        get
        {
            lock (_lock)
            {
                return _results.Count;
            }
        }
    }

    /// <summary>True when the script emitted more objects than are kept (Runner.MaxResultRows).</summary>
    public bool ResultsTruncated { get; private set; }

    public IReadOnlyList<ResultRow> Results()
    {
        lock (_lock)
        {
            return [.. _results];
        }
    }

    internal void AddResult(ResultRow row)
    {
        lock (_lock)
        {
            _results.Add(row);
        }
    }

    internal void MarkResultsTruncated() => ResultsTruncated = true;

    public int WarningCount { get; private set; }

    public bool IsFinished => EndedAt is not null;

    public TimeSpan? Duration => StartedAt is { } start ? (EndedAt ?? _time.GetUtcNow()) - start : null;

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
    }

    CancellationToken IRunReporter.CancellationToken => CancellationToken;

    void IRunReporter.MarkRunning() => MarkRunning();

    void IRunReporter.Append(OutputKind stream, string? text) => Append(stream, text);

    void IRunReporter.AddResult(ResultRow row) => AddResult(row);

    void IRunReporter.MarkResultsTruncated() => MarkResultsTruncated();

    void IRunReporter.Complete(RunStatus status) => Complete(status);
}
