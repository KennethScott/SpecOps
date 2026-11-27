using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Management.Automation;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SpecOps.Core.Scripts;

namespace SpecOps.Core.Execution;

/// <summary>
/// The script worker: a separate process that runs scripts for the web app, so a script that crashes, hangs or leaks
/// memory can't take the web app down with it. The web app starts it (see <see cref="WorkerExecutor"/>) and talks to
/// it over a named pipe that only the same account can open. It runs until the web app closes the pipe.
/// </summary>
public static class ScriptWorker
{
    public static async Task RunAsync(string pipeName, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        await pipe.ConnectAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);

        using var connection = new WorkerConnection(pipe);
        // Not disposed: when the web app goes, so does this process, runspaces and all.
        var executor = new PowerShellExecutor(loggerFactory.CreateLogger("SpecOps.Worker"), TimeProvider.System);
        var runs = new ConcurrentDictionary<Guid, WorkerRun>();
        try
        {
            await foreach (var message in connection.ReadAllAsync().ConfigureAwait(false))
            {
                if (message is StartRun start)
                {
                    var run = new WorkerRun(start.RunId, connection);
                    runs[start.RunId] = run;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await executor
                                .ExecuteAsync(start.RunId, start.Request.ToRunRequest(), start.Settings, run)
                                .ConfigureAwait(false);
                        }
#pragma warning disable CA1031 // The web app is waiting for this run's final status, whatever happened.
                        catch (Exception ex)
#pragma warning restore CA1031
                        {
                            run.Append(OutputKind.Error, $"Unexpected error: {ex.Message}");
                            run.Complete(RunStatus.Failed);
                        }
                        finally
                        {
                            runs.TryRemove(start.RunId, out _);
                            run.Dispose();
                        }
                    });
                }
                else if (message is CancelRun cancel && runs.TryGetValue(cancel.RunId, out var active))
                {
                    active.Cancel();
                }
            }
        }
        catch (IOException)
        {
            // The web app has gone.
        }
    }
}

/// <summary>A run in the worker: progress goes straight back to the web app.</summary>
internal sealed class WorkerRun(Guid id, WorkerConnection connection) : IRunReporter, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private int _errors;
    private int _results;
    private int _truncated;
    private int _completed;
    private volatile bool _cancelRequested;

    public CancellationToken CancellationToken => _cancellation.Token;

    public bool CancelRequested => _cancelRequested;

    public int ErrorCount => Volatile.Read(ref _errors);

    public int ResultCount => Volatile.Read(ref _results);

    public void Cancel()
    {
        _cancelRequested = true;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished in the meantime.
        }
    }

    public void MarkRunning() => connection.Send(new RunStarted(id));

    public void Append(OutputKind stream, string? text)
    {
        if (stream == OutputKind.Error)
        {
            Interlocked.Increment(ref _errors);
        }

        connection.Send(new RunOutput(id, stream, text ?? ""));
    }

    public void AddResult(ResultRow row)
    {
        Interlocked.Increment(ref _results);
        connection.Send(new RunResult(id, row));
    }

    public void MarkResultsTruncated()
    {
        if (Interlocked.Exchange(ref _truncated, 1) == 0)
        {
            connection.Send(new RunResultsTruncated(id));
        }
    }

    public void Complete(RunStatus status)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            connection.Send(new RunFinished(id, status));
        }
    }

    public void Dispose() => _cancellation.Dispose();
}

// ---- Messages between the web app and the worker: one JSON object per line. ----

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StartRun), "start")]
[JsonDerivedType(typeof(CancelRun), "cancel")]
[JsonDerivedType(typeof(RunStarted), "running")]
[JsonDerivedType(typeof(RunOutput), "output")]
[JsonDerivedType(typeof(RunResult), "result")]
[JsonDerivedType(typeof(RunResultsTruncated), "truncated")]
[JsonDerivedType(typeof(RunFinished), "finished")]
internal abstract record WorkerMessage(Guid RunId);

internal sealed record StartRun(Guid RunId, WorkerRequest Request, ExecutionSettings Settings) : WorkerMessage(RunId);

internal sealed record CancelRun(Guid RunId) : WorkerMessage(RunId);

internal sealed record RunStarted(Guid RunId) : WorkerMessage(RunId);

internal sealed record RunOutput(Guid RunId, OutputKind Stream, string Text) : WorkerMessage(RunId);

internal sealed record RunResult(Guid RunId, ResultRow Row) : WorkerMessage(RunId);

internal sealed record RunResultsTruncated(Guid RunId) : WorkerMessage(RunId);

internal sealed record RunFinished(Guid RunId, RunStatus Status) : WorkerMessage(RunId);

/// <summary>What the worker needs from a <see cref="ScriptRunRequest"/>, with parameter values as typed text.</summary>
internal sealed record WorkerRequest(
    string ScriptId,
    string ScriptName,
    string? ScriptPath,
    string? Code,
    IReadOnlyList<WorkerParameter> Parameters,
    RunspaceProfile? RunspaceProfile,
    string UserName,
    string? ClientIp
)
{
    public static WorkerRequest From(ScriptRunRequest request) =>
        new(
            request.ScriptId,
            request.ScriptName,
            request.ScriptPath,
            request.Code,
            [.. request.Parameters.Select(p => WorkerParameter.From(p.Key, p.Value))],
            request.RunspaceProfile,
            request.UserName,
            request.ClientIp
        );

    public ScriptRunRequest ToRunRequest() =>
        new()
        {
            ScriptId = ScriptId,
            ScriptName = ScriptName,
            ScriptPath = ScriptPath,
            Code = Code,
            Parameters = Parameters.ToDictionary(p => p.Name, p => p.ToValue(), StringComparer.OrdinalIgnoreCase),
            RunspaceProfile = RunspaceProfile,
            UserName = UserName,
            ClientIp = ClientIp,
        };
}

/// <summary>A parameter value of one of the types <see cref="Validation.ParameterValidator"/> produces.</summary>
internal sealed record WorkerParameter(string Name, string Type, string Value, string? UserName = null)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static WorkerParameter From(string name, object value) =>
        value switch
        {
            string text => new(name, "string", text),
            int number => new(name, "int", number.ToString(Invariant)),
            double number => new(name, "double", number.ToString("R", Invariant)),
            bool flag => new(name, "bool", flag ? "true" : "false"),
            DateTime dateTime => new(name, "datetime", dateTime.ToString("O", Invariant)),
            TimeSpan time => new(name, "timespan", time.ToString("c", Invariant)),
            PSCredential credential => new(
                name,
                "credential",
                credential.GetNetworkCredential().Password,
                credential.UserName
            ),
            _ => throw new NotSupportedException(
                $"Parameter '{name}' has a value of type {value?.GetType().Name ?? "null"}, which can't be passed to the script worker."
            ),
        };

    public object ToValue() =>
        Type switch
        {
            "string" => Value,
            "int" => int.Parse(Value, Invariant),
            "double" => double.Parse(Value, Invariant),
            "bool" => string.Equals(Value, "true", StringComparison.Ordinal),
            "datetime" => DateTime.Parse(Value, Invariant, DateTimeStyles.RoundtripKind),
            "timespan" => TimeSpan.ParseExact(Value, "c", Invariant),
            "credential" => Credential(UserName ?? "", Value),
            _ => throw new NotSupportedException($"Unknown parameter type '{Type}'."),
        };

    private static PSCredential Credential(string userName, string password)
    {
        var secure = new NetworkCredential("", password).SecurePassword;
        secure.MakeReadOnly();
        return new PSCredential(userName, secure);
    }
}

/// <summary>Newline-delimited JSON messages over a pipe. Sends from any thread are queued and written in order.</summary>
internal sealed class WorkerConnection : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Channel<WorkerMessage> _outbox = Channel.CreateUnbounded<WorkerMessage>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly Task _sending;

    public WorkerConnection(Stream stream)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _reader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(stream, utf8);
        _sending = Task.Run(SendAllAsync);
    }

    public void Send(WorkerMessage message) => _outbox.Writer.TryWrite(message);

    /// <summary>Messages from the other side, until it closes the pipe.</summary>
    public async IAsyncEnumerable<WorkerMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        while (await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (JsonSerializer.Deserialize<WorkerMessage>(line, Json) is { } message)
            {
                yield return message;
            }
        }
    }

    private async Task SendAllAsync()
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out var message))
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(message, Json)).ConfigureAwait(false);
                }

                await _writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The other side has gone; reading notices that too.
        }
    }

    /// <summary>Sends what's queued (briefly), then closes the pipe.</summary>
    public void Dispose()
    {
        _outbox.Writer.TryComplete();
        _sending.Wait(TimeSpan.FromSeconds(5));
        try
        {
            _writer.Dispose();
        }
        catch (IOException)
        {
            // Flushing to a pipe the other side already closed.
        }

        _reader.Dispose();
    }
}
