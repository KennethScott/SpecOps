using Microsoft.Extensions.Logging.Abstractions;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;

namespace SpecOps.Tests.Core;

/// <summary>Concurrency limits, live reconfiguration, shutdown and pool retirement.</summary>
public class RunLifecycleTests
{
    private static ScriptRunRequest Code(string code, string user = "tester") =>
        new()
        {
            ScriptId = "t",
            ScriptName = "T",
            Code = code,
            UserName = user,
        };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    [Fact]
    public async Task Gate_is_first_in_first_out_and_honours_a_raised_limit()
    {
        var limit = 1;
        var gate = new RunGate(() => limit);
        var ct = TestContext.Current.CancellationToken;

        var first = await gate.EnterAsync(ct);
        var second = gate.EnterAsync(ct);
        var third = gate.EnterAsync(ct);
        Assert.False(second.IsCompleted);

        // Raising the limit admits the next in line (second), not third.
        limit = 2;
        gate.Pump();
        var secondSlot = await second;
        Assert.NotSame(third, await Task.WhenAny(third, Task.Delay(200, ct)));
        Assert.Equal(2, gate.Active);

        // Freeing a slot admits third.
        first.Dispose();
        (await third).Dispose();
        secondSlot.Dispose();
        Assert.Equal(0, gate.Active);
    }

    [Fact]
    public async Task Cancelling_a_queued_entry_frees_its_place()
    {
        var gate = new RunGate(() => 1);
        var ct = TestContext.Current.CancellationToken;
        using var held = await gate.EnterAsync(ct);
        using var cts = new CancellationTokenSource();

        var queued = gate.EnterAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, gate.Active);
    }

    [Fact]
    public async Task Raising_MaxConcurrentRuns_admits_queued_runs_immediately()
    {
        var options = TestOptions.Create(o => o.Runner.MaxConcurrentRuns = 1);
        using var runner = new ScriptRunner(options, NullLogger<ScriptRunner>.Instance);

        var blocker = runner.Start(Code("Start-Sleep -Seconds 30", "a"));
        await WaitForAsync(() => blocker.Status == RunStatus.Running);
        var queued = runner.Start(Code("'done'", "b"));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(RunStatus.Queued, queued.Status);

        options.Update(o => o.Runner.MaxConcurrentRuns = 2);

        await WaitForAsync(() => queued.IsFinished);
        Assert.Equal(RunStatus.Succeeded, queued.Status);
        runner.Cancel(blocker.Id);
    }

    [Fact]
    public async Task StopAsync_cancels_runs_and_refuses_new_ones()
    {
        using var runner = new ScriptRunner(TestOptions.Create(), NullLogger<ScriptRunner>.Instance);
        var running = runner.Start(Code("Start-Sleep -Seconds 30"));
        await WaitForAsync(() => running.Status == RunStatus.Running);

        await runner.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Cancelled, running.Status);
        Assert.Throws<RunRejectedException>(() => runner.Start(Code("'too late'")));
    }

    [Fact]
    public async Task Oldest_finished_runs_are_dropped_when_the_output_budget_is_exceeded()
    {
        // Each run below holds 5 records: "Starting...", 3 output lines, "Finished...".
        using var runner = new ScriptRunner(
            TestOptions.Create(o => o.Runner.MaxRetainedOutputRecords = 6),
            NullLogger<ScriptRunner>.Instance
        );
        var first = runner.Start(Code("1..3"));
        await WaitForAsync(() => first.IsFinished);
        var second = runner.Start(Code("1..3", "other"));
        await WaitForAsync(() => second.IsFinished);
        Assert.Equal(5, first.RecordCount);

        var third = runner.Start(Code("'x'", "third"));

        Assert.Null(runner.Find(first.Id));
        Assert.NotNull(runner.Find(second.Id));
        Assert.NotNull(runner.Find(third.Id));
        await WaitForAsync(() => third.IsFinished);
    }

    [Fact]
    public void Categories_are_cached_until_the_configuration_changes()
    {
        var options = TestOptions.Create(o =>
            o.Scripts.Add(
                new ScriptDefinition
                {
                    Id = "a",
                    Name = "A",
                    CategoryId = "ops",
                }
            )
        );
        var catalog = new ConfigurationScriptCatalog(options);

        var first = catalog.Categories;
        Assert.Same(first, catalog.Categories);

        var reloaded = new SpecOps.Core.Configuration.SpecOpsOptions();
        reloaded.Scripts.Add(
            new ScriptDefinition
            {
                Id = "b",
                Name = "B",
                CategoryId = "new",
            }
        );
        options.Set(reloaded);

        Assert.NotSame(first, catalog.Categories);
        Assert.Equal("new", Assert.Single(catalog.Categories).Id);
    }

    [Fact]
    public void Cancelling_a_finished_run_is_a_no_op()
    {
        using var runner = new ScriptRunner(TestOptions.Create(), NullLogger<ScriptRunner>.Instance);

        Assert.False(runner.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_changed_runspace_profile_retires_the_old_pool()
    {
        using var pools = new RunspacePoolCache();
        var original = new RunspaceProfile
        {
            Name = "p",
            MinRunspaces = 0,
            MaxRunspaces = 1,
        };
        var oldPool = pools.Get(original, null);

        var changed = new RunspaceProfile
        {
            Name = "p",
            MinRunspaces = 0,
            MaxRunspaces = 2,
        };
        var newPool = pools.Get(changed, null);

        Assert.NotSame(oldPool, newPool);
        Assert.Same(newPool, pools.Get(changed, null));
        Assert.Equal(1, pools.PoolCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            oldPool.RentAsync(TestContext.Current.CancellationToken)
        );
    }
}
