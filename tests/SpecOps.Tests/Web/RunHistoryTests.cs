using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SpecOps.Core.Configuration;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;
using SpecOps.Data;
using SpecOps.Web.Services;

namespace SpecOps.Tests.Web;

/// <summary>Runs saved to the database: written by the runner, read back by the pages, cleaned up by retention.</summary>
public sealed class RunHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;
    private readonly TestOptions<SpecOpsOptions> _options = TestOptions.Create();
    private readonly RunHistory _history;

    public RunHistoryTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<SpecOpsDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        using (var scope = _services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>().Database.EnsureCreated();
        }

        _history = new RunHistory(Scopes, _options, TimeProvider.System, NullLogger<RunHistory>.Instance);
    }

    private IServiceScopeFactory Scopes => _services.GetRequiredService<IServiceScopeFactory>();

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_finished_run_is_stored_with_its_output_and_results()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptRunner(_options, NullLogger<ScriptRunner>.Instance, history: _history);
        var run = runner.Start(
            new ScriptRunRequest
            {
                ScriptId = "terminal",
                ScriptName = "Terminal",
                Code = "Write-Warning 'careful'; [pscustomobject]@{ Name = 'alpha'; Size = 1 }",
                UserName = "admin@contoso.com",
                UserId = "user-1",
                ClientIp = "10.0.0.5",
            }
        );
        await foreach (var _ in run.WatchAsync(ct)) { }

        // Waits for the final history write, then the runner (and its memory) is gone.
        await runner.StopAsync(ct);
        runner.Dispose();

        var stored = await _history.FindAsync(run.Id);
        Assert.NotNull(stored);
        Assert.Equal(RunStatus.Succeeded, stored.Status);
        Assert.Equal("admin@contoso.com", stored.UserName);
        Assert.Equal("10.0.0.5", stored.ClientIp);
        Assert.Equal(run.Code, stored.Code);
        Assert.Equal(1, stored.WarningCount);
        Assert.Contains(stored.Output, r => r.Stream == OutputKind.Warning && r.Text == "careful");
        var row = Assert.Single(stored.Results);
        Assert.Equal("alpha", row["Name"]);

        var reopened = ScriptRun.FromSnapshot(stored);
        Assert.True(reopened.IsFinished);
        Assert.Equal(run.Snapshot().Select(r => r.Text), reopened.Snapshot().Select(r => r.Text));
        Assert.Equal(1, reopened.ResultCount);
    }

    [Fact]
    public async Task Runs_cut_off_by_a_restart_are_marked_interrupted()
    {
        var queuedAt = DateTime.UtcNow.AddMinutes(-5);
        await AddAsync(Record("user-1", queuedAt, status: RunStatus.Running));

        Assert.Equal(1, await _history.MarkInterruptedAsync());

        var stored = (await _history.ListAsync(null, 10)).Single();
        Assert.Equal(RunStatus.Interrupted, stored.Status);
        var snapshot = await _history.FindAsync(stored.Id);
        Assert.Contains("wasn't saved", Assert.Single(snapshot!.Output).Text, StringComparison.Ordinal);
        Assert.Equal(0, await _history.MarkInterruptedAsync());
    }

    [Fact]
    public async Task Cleanup_deletes_finished_runs_past_retention()
    {
        _options.Update(o => o.Runner.HistoryRetention = TimeSpan.FromDays(30));
        var old = DateTime.UtcNow.AddDays(-31);
        await AddAsync(
            Record("user-1", old),
            Record("user-1", old, status: RunStatus.Running),
            Record("user-1", DateTime.UtcNow.AddDays(-29))
        );

        Assert.Equal(1, await _history.PruneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, (await _history.ListAsync(null, 10)).Count);
    }

    [Fact]
    public async Task Users_see_their_own_history_and_admins_see_everyones()
    {
        await AddAsync(
            Record("user-1", DateTime.UtcNow.AddMinutes(-2)),
            Record("user-2", DateTime.UtcNow.AddMinutes(-1))
        );
        using var runner = new ScriptRunner(_options, NullLogger<ScriptRunner>.Instance, history: _history);
        var service = new ScriptService(
            new ConfigurationScriptCatalog(_options),
            new SpecOpsAuthorizer(_options),
            runner,
            new AuditLog(Scopes, NullLogger<AuditLog>.Instance),
            _history,
            new CredentialVault(
                Scopes,
                new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
                new SpecOpsAuthorizer(_options),
                new AuditLog(Scopes, NullLogger<AuditLog>.Instance),
                TimeProvider.System
            )
        );

        var mine = await service.GetRunsAsync(User("user-1"), 10);
        var all = await service.GetRunsAsync(User("user-9", SpecOpsAuthorizer.AdminRole), 10);

        var only = Assert.Single(mine);
        Assert.NotNull(await service.FindRunAsync(User("user-1"), only.Id));
        Assert.Null(await service.FindRunAsync(User("user-2"), only.Id));
        Assert.Equal(["user-2", "user-1"], all.Select(r => r.UserName));
    }

    private async Task AddAsync(params RunRecord[] records)
    {
        await using var scope = Scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        db.Runs.AddRange(records);
        await db.SaveChangesAsync();
    }

    private static RunRecord Record(string userId, DateTime queuedAt, RunStatus status = RunStatus.Succeeded) =>
        new()
        {
            Id = Guid.NewGuid(),
            ScriptId = "demo",
            ScriptName = "Demo",
            UserId = userId,
            UserName = userId,
            QueuedAtUtc = queuedAt,
            StartedAtUtc = queuedAt,
            EndedAtUtc = status is RunStatus.Running ? null : queuedAt.AddSeconds(3),
            Status = status.ToString(),
            OutputJson = "[]",
        };

    private static ClaimsPrincipal User(string id, params string[] roles) =>
        new(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id), .. roles.Select(r => new Claim(ClaimTypes.Role, r))],
                "test"
            )
        );
}
