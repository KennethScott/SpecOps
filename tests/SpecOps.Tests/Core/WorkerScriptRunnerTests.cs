using SpecOps.Core.Execution;
using SpecOps.Web.Infrastructure;

namespace SpecOps.Tests.Core;

/// <summary>Every <see cref="ScriptRunnerTests"/> test again, with scripts running in the script worker process.</summary>
public sealed class WorkerScriptRunnerTests : ScriptRunnerTests
{
    public WorkerScriptRunnerTests()
        : base(ScriptWorkerSetup.Command()) { }

    [Fact]
    public async Task A_script_that_kills_the_worker_fails_its_run_and_the_next_run_gets_a_new_worker()
    {
        var crash = Runner.Start(Code("'about to exit'; [Environment]::Exit(3)"));
        var records = await CompleteAsync(crash);

        Assert.Equal(RunStatus.Failed, crash.Status);
        Assert.Contains(Lines(records, OutputKind.Error), e =>
            e.Contains("stopped unexpectedly (exit code 3)", StringComparison.Ordinal)
        );

        var next = Runner.Start(Code("'still here'"));
        Assert.Equal(["still here"], Lines(await CompleteAsync(next), OutputKind.Output));
        Assert.Equal(RunStatus.Succeeded, next.Status);
    }

    [Fact]
    public async Task Scripts_run_in_another_process()
    {
        var run = Runner.Start(Code("$PID"));

        var output = Lines(await CompleteAsync(run), OutputKind.Output);

        Assert.NotEqual(
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            output.Single()
        );
    }
}
