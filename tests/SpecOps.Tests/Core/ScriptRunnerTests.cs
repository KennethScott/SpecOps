using Microsoft.Extensions.Logging.Abstractions;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;

namespace SpecOps.Tests.Core;

/// <summary>Runs scripts in this process; <see cref="WorkerScriptRunnerTests"/> runs the same tests through the worker.</summary>
public class ScriptRunnerTests : IDisposable
{
    private readonly ScriptRunner _runner;

    public ScriptRunnerTests()
        : this(worker: null) { }

    protected ScriptRunnerTests(ScriptWorkerCommand? worker) =>
        _runner = new(
            TestOptions.Create(o => o.Runner.MaxConcurrentRuns = 4),
            NullLogger<ScriptRunner>.Instance,
            worker: worker
        );

    protected ScriptRunner Runner => _runner;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runner.Dispose();
        }
    }

    protected static ScriptRunRequest Code(
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

    protected static async Task<IReadOnlyList<OutputRecord>> CompleteAsync(ScriptRun run)
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

    protected static string[] Lines(IEnumerable<OutputRecord> records, OutputKind kind) =>
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
    public async Task Passes_every_parameter_type_including_credentials()
    {
        var password = new System.Net.NetworkCredential("", "Pa55word!").SecurePassword;
        var run = _runner.Start(
            Code(
                """
                param([pscredential]$Account, [double]$Ratio, [datetime]$When, [timespan]$At, [bool]$Flag)
                "$($Account.UserName):$($Account.GetNetworkCredential().Password)"
                $Ratio.ToString([cultureinfo]::InvariantCulture)
                $When.ToString("o")
                "$At $Flag"
                """,
                new Dictionary<string, object>
                {
                    ["Account"] = new System.Management.Automation.PSCredential("svc-account", password),
                    ["Ratio"] = 0.25,
                    ["When"] = new DateTime(2026, 11, 26, 9, 30, 0, DateTimeKind.Unspecified),
                    ["At"] = new TimeSpan(14, 5, 0),
                    ["Flag"] = false,
                }
            )
        );

        var records = await CompleteAsync(run);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(
            ["svc-account:Pa55word!", "0.25", "2026-11-26T09:30:00.0000000", "14:05:00 False"],
            Lines(records, OutputKind.Output)
        );
        Assert.Equal("svc-account", run.Parameters["Account"]);
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
    public async Task Pooled_runspaces_are_reused_but_reset_between_runs()
    {
        // One runspace, so the second run is guaranteed to reuse it.
        var profile = new RunspaceProfile
        {
            Name = "reset-test",
            MinRunspaces = 1,
            MaxRunspaces = 1,
        };
        ScriptRunRequest Pooled(string code) => Code(code) with { RunspaceProfile = profile };

        var first = _runner.Start(
            Pooled(
                """
                $global:Leak = 'secret'
                function global:Get-Leak { 'leaked' }
                Set-Alias -Name leak -Value Get-Date -Scope Global
                New-PSDrive -Name LeakDrive -PSProvider FileSystem -Root ([IO.Path]::GetTempPath()) -Scope Global | Out-Null
                Set-Location ([IO.Path]::GetTempPath())
                $ErrorActionPreference = 'Stop'
                "id=$([runspace]::DefaultRunspace.InstanceId)"
                """
            )
        );
        var firstOutput = Lines(await CompleteAsync(first), OutputKind.Output);

        var second = _runner.Start(
            Pooled(
                """
                "variable=$([bool](Get-Variable Leak -Scope Global -ErrorAction SilentlyContinue))"
                "function=$([bool](Get-Command Get-Leak -ErrorAction SilentlyContinue))"
                "alias=$([bool](Get-Alias leak -ErrorAction SilentlyContinue))"
                "drive=$([bool](Get-PSDrive LeakDrive -ErrorAction SilentlyContinue))"
                "preference=$($global:ErrorActionPreference)"
                "id=$([runspace]::DefaultRunspace.InstanceId)"
                """
            )
        );
        var secondOutput = Lines(await CompleteAsync(second), OutputKind.Output);

        Assert.Equal(RunStatus.Succeeded, first.Status);
        Assert.Equal(firstOutput.Single(l => l.StartsWith("id=", StringComparison.Ordinal)), secondOutput[^1]);
        Assert.Equal(
            ["variable=False", "function=False", "alias=False", "drive=False", "preference=Continue"],
            secondOutput[..^1]
        );
    }

    [Fact]
    public async Task Rejects_runs_beyond_the_per_user_active_limit()
    {
        using var runner = new ScriptRunner(
            TestOptions.Create(o => o.Runner.MaxActiveRunsPerUser = 1),
            NullLogger<ScriptRunner>.Instance
        );
        var running = runner.Start(Code("Start-Sleep -Seconds 30"));

        var ex = Assert.Throws<RunRejectedException>(() => runner.Start(Code("'second'")));
        Assert.Contains("in progress", ex.Message, StringComparison.Ordinal);

        // Someone else is unaffected.
        var other = runner.Start(Code("'other user'") with { UserName = "someone-else" });

        runner.Cancel(running.Id);
        await CompleteAsync(running);
        await CompleteAsync(other);
        Assert.Equal(RunStatus.Succeeded, other.Status);
    }

    [Fact]
    public async Task Rejects_runs_beyond_the_per_user_rate()
    {
        using var runner = new ScriptRunner(
            TestOptions.Create(o => o.Runner.MaxRunsPerMinutePerUser = 2),
            NullLogger<ScriptRunner>.Instance
        );
        var a = runner.Start(Code("'a'"));
        var b = runner.Start(Code("'b'"));

        Assert.Throws<RunRejectedException>(() => runner.Start(Code("'c'")));
        await CompleteAsync(a);
        await CompleteAsync(b);
    }

    [Fact]
    public async Task Captures_emitted_objects_as_result_rows_without_changing_the_text()
    {
        var run = _runner.Start(
            Code(
                """
                'just text'
                42
                [pscustomobject]@{ Name = 'alpha'; Size = 1; Tags = @('a', 'b') }
                [pscustomobject]@{ Name = 'beta'; Size = 22 }
                Get-Item -LiteralPath ([IO.Path]::GetTempPath())
                """
            )
        );

        var records = await CompleteAsync(run);
        var rows = run.Results();

        Assert.Equal(3, rows.Count);
        var objects = ResultSet.Group(rows).Single(s => s.Rows.Count == 2);
        Assert.Equal(["Name", "Size", "Tags"], objects.Columns);
        Assert.Equal("alpha", objects.Rows[0]["Name"]);
        Assert.Equal("a, b", objects.Rows[0]["Tags"]);
        Assert.Equal("22", objects.Rows[1]["Size"]);

        // Same columns as Format-Table (the type's table view), not every property.
        var directory = rows.Single(r => r.TypeName.Contains("DirectoryInfo", StringComparison.Ordinal));
        Assert.InRange(directory.Columns.Count, 1, 10);
        // Windows shows "Mode", Linux and macOS "UnixMode" (plus User and Group).
        Assert.Contains(directory.Columns, c => c is "Mode" or "UnixMode");
        Assert.Contains("Name", directory.Columns);

        // The console still shows everything as it would in a terminal.
        var text = string.Join('\n', Lines(records, OutputKind.Output));
        Assert.Contains("just text", text, StringComparison.Ordinal);
        Assert.Contains("alpha", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Formatted_output_and_plain_values_are_not_captured_and_rows_are_capped()
    {
        using var runner = new ScriptRunner(
            TestOptions.Create(o => o.Runner.MaxResultRows = 2),
            NullLogger<ScriptRunner>.Instance
        );

        var formatted = runner.Start(Code("[pscustomobject]@{ A = 1 } | Format-Table; 'text'; 1..3"));
        await CompleteAsync(formatted);
        Assert.Equal(0, formatted.ResultCount);

        var many = runner.Start(Code("1..5 | ForEach-Object { [pscustomobject]@{ N = $_ } }"));
        await CompleteAsync(many);
        Assert.Equal(2, many.ResultCount);
        Assert.True(many.ResultsTruncated);
    }

    [Fact]
    public async Task Keeps_terminal_code_on_the_run()
    {
        var run = _runner.Start(Code("'hi'"));
        await CompleteAsync(run);

        Assert.Equal("'hi'", run.Code);
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
