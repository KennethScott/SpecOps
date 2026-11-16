using Microsoft.Extensions.Logging.Abstractions;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;

namespace SpecOps.Tests.Core;

public sealed class ScriptRunnerTests : IDisposable
{
    private readonly ScriptRunner _runner = new(
        TestOptions.Create(o => o.Runner.MaxConcurrentRuns = 4),
        NullLogger<ScriptRunner>.Instance
    );

    public void Dispose() => _runner.Dispose();

    private static ScriptRunRequest Code(
        string code,
        IReadOnlyDictionary<string, object>? parameters = null,
        TimeSpan? timeout = null
    ) =>
        new()
        {
            ScriptId = "test",
            ScriptName = "Test",
            Code = code,
            Parameters = parameters ?? new Dictionary<string, object>(),
            UserName = "tester",
            ClientIp = "127.0.0.1",
            Timeout = timeout,
        };

    private static async Task<IReadOnlyList<OutputRecord>> CompleteAsync(ScriptRun run)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var records = new List<OutputRecord>();
        try
        {
            await foreach (var record in run.WatchAsync(cts.Token))
            {
                records.Add(record);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(
                $"Run didn't finish (status {run.Status}). Output:\n{string.Join('\n', run.Snapshot().Select(r => $"[{r.Stream}] {r.Text}"))}"
            );
        }

        return records;
    }

    private static string[] Lines(IEnumerable<OutputRecord> records, OutputKind kind) =>
        [.. records.Where(r => r.Stream == kind).Select(r => r.Text)];

    [Fact]
    public async Task Captures_every_stream_in_order()
    {
        var run = _runner.Start(
            Code(
                """
                'out'
                Write-Information 'info'
                Write-Warning 'warn'
                Write-Verbose 'verbose' -Verbose
                Write-Error 'oops'
                """
            )
        );

        var records = await CompleteAsync(run);

        Assert.Equal(RunStatus.CompletedWithErrors, run.Status);
        Assert.Equal(["out"], Lines(records, OutputKind.Output));
        Assert.Equal(["info"], Lines(records, OutputKind.Information));
        Assert.Equal(["warn"], Lines(records, OutputKind.Warning));
        Assert.Equal(["verbose"], Lines(records, OutputKind.Verbose));
        Assert.Single(Lines(records, OutputKind.Error));
        Assert.Equal(1, run.ErrorCount);
        Assert.Equal(1, run.WarningCount);
        Assert.True(records.Select(r => r.Sequence).SequenceEqual(records.Select(r => r.Sequence).Order()));
    }

    [Fact]
    public async Task Passes_typed_parameters_and_context_variable()
    {
        var run = _runner.Start(
            Code(
                """
                [CmdletBinding()]
                param([string]$Name, [int]$Count, [switch]$Loud)
                "$Name x $Count loud=$Loud user=$($SpecOps.User) ip=$($SpecOps.ClientIp)"
                """,
                new Dictionary<string, object>
                {
                    ["Name"] = "Widget",
                    ["Count"] = 3,
                    ["Loud"] = true,
                }
            )
        );

        var records = await CompleteAsync(run);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(["Widget x 3 loud=True user=tester ip=127.0.0.1"], Lines(records, OutputKind.Output));
    }

    [Fact]
    public async Task Formats_objects_like_the_console()
    {
        var run = _runner.Start(Code("[pscustomobject]@{ Name = 'alpha'; Size = 42 }"));

        var output = string.Join('\n', Lines(await CompleteAsync(run), OutputKind.Output));

        Assert.Contains("Name", output, StringComparison.Ordinal);
        Assert.Contains("alpha", output, StringComparison.Ordinal);
        Assert.Contains("42", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminating_error_fails_the_run()
    {
        var run = _runner.Start(Code("throw 'boom'"));

        var records = await CompleteAsync(run);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains(Lines(records, OutputKind.Error), e => e.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_actually_stops_the_pipeline()
    {
        var run = _runner.Start(Code("'started'; Start-Sleep -Seconds 30; 'should not get here'"));
        await foreach (var record in run.WatchAsync(TestContext.Current.CancellationToken))
        {
            if (record.Text == "started")
            {
                break;
            }
        }

        Assert.True(_runner.Cancel(run.Id));
        var records = await CompleteAsync(run);

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.DoesNotContain("should not get here", Lines(records, OutputKind.Output));
        Assert.True(run.Duration < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Timeout_stops_the_run()
    {
        var run = _runner.Start(Code("Start-Sleep -Seconds 30", timeout: TimeSpan.FromSeconds(2)));

        await CompleteAsync(run);

        Assert.Equal(RunStatus.TimedOut, run.Status);
    }

    [Fact]
    public async Task Late_watchers_get_the_full_backlog()
    {
        var run = _runner.Start(Code("1..3"));
        var first = await CompleteAsync(run);

        var replay = await CompleteAsync(run);

        Assert.Equal(first, replay);
        Assert.Equal(["1", "2", "3"], Lines(replay, OutputKind.Output));
    }

    [Fact]
    public async Task Runs_from_a_pooled_runspace_profile()
    {
        var request = Code("'pooled'");
        var pooled = new ScriptRunRequest
        {
            ScriptId = request.ScriptId,
            ScriptName = request.ScriptName,
            Code = request.Code,
            UserName = request.UserName,
            RunspaceProfile = new RunspaceProfile
            {
                Name = "test",
                MinRunspaces = 1,
                MaxRunspaces = 2,
            },
        };

        var first = _runner.Start(pooled);
        var second = _runner.Start(pooled);

        Assert.Equal(["pooled"], Lines(await CompleteAsync(first), OutputKind.Output));
        Assert.Equal(["pooled"], Lines(await CompleteAsync(second), OutputKind.Output));
    }

    [Fact]
    public async Task Runs_script_files_with_PSScriptRoot_and_strict_params()
    {
        var dir = Directory.CreateTempSubdirectory("specops-tests-");
        try
        {
            var path = Path.Combine(dir.FullName, "Hello.ps1");
            await File.WriteAllTextAsync(
                path,
                """
                [CmdletBinding()]
                param([Parameter(Mandatory)][string]$Name)
                "Hello $Name from $(Split-Path -Leaf $PSScriptRoot)"
                """,
                TestContext.Current.CancellationToken
            );

            var run = _runner.Start(
                new ScriptRunRequest
                {
                    ScriptId = "hello",
                    ScriptName = "Hello",
                    ScriptPath = path,
                    Parameters = new Dictionary<string, object> { ["Name"] = "World" },
                    UserName = "tester",
                }
            );

            var records = await CompleteAsync(run);

            Assert.Equal(RunStatus.Succeeded, run.Status);
            Assert.Equal([$"Hello World from {dir.Name}"], Lines(records, OutputKind.Output));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Sensitive_parameters_are_masked()
    {
        var run = _runner.Start(
            new ScriptRunRequest
            {
                ScriptId = "t",
                ScriptName = "t",
                Code = "param($Password) 'ok'",
                Parameters = new Dictionary<string, object> { ["Password"] = "hunter2" },
                SensitiveParameters = new HashSet<string>(["Password"], StringComparer.OrdinalIgnoreCase),
                UserName = "tester",
            }
        );

        Assert.Equal(ScriptRunRequest.MaskedValue, run.Parameters["Password"]);
    }
}
