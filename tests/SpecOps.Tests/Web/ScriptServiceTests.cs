using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;
using SpecOps.Data;
using SpecOps.Web.Services;

namespace SpecOps.Tests.Web;

/// <summary>The authorization gate every run goes through, plus its audit trail.</summary>
public sealed class ScriptServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;
    private readonly ScriptRunner _runner;
    private readonly RunHistory _history;
    private readonly ScriptService _service;

    public ScriptServiceTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<SpecOpsDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        using (var scope = _services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>().Database.EnsureCreated();
        }

        var options = TestOptions.Create(o =>
        {
            o.ScriptsRoot = AppContext.BaseDirectory;
            o.Policies.Add(
                new PolicyDefinition
                {
                    Name = "Help desk",
                    Roles = ["HelpDesk"],
                    Categories = ["demo"],
                }
            );
            o.Scripts.Add(
                new ScriptDefinition
                {
                    Id = "demo-script",
                    Name = "Demo",
                    CategoryId = "demo",
                    Path = "x.ps1",
                }
            );
            o.Scripts.Add(
                new ScriptDefinition
                {
                    Id = "admin-script",
                    Name = "Admin",
                    CategoryId = "admin",
                    Path = "x.ps1",
                }
            );
        });
        _history = new RunHistory(
            _services.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<RunHistory>.Instance
        );
        _runner = new ScriptRunner(options, NullLogger<ScriptRunner>.Instance, history: _history);
        _service = new ScriptService(
            new ConfigurationScriptCatalog(options),
            new SpecOpsAuthorizer(options),
            _runner,
            new AuditLog(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditLog>.Instance),
            _history
        );
    }

    public void Dispose()
    {
        _runner.Dispose();
        _services.Dispose();
        _connection.Dispose();
    }

    private static ClaimsPrincipal User(params string[] roles) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim(ClaimTypes.Name, "helpdesk@contoso.com"),
                    .. roles.Select(r => new Claim(ClaimTypes.Role, r)),
                ],
                "test"
            )
        );

    private List<AuditEvent> AuditEvents()
    {
        using var scope = _services.CreateScope();
        return [.. scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>().AuditEvents.AsNoTracking()];
    }

    [Fact]
    public async Task Refuses_and_audits_scripts_outside_the_users_categories()
    {
        var result = await _service.StartScriptAsync(
            User("HelpDesk"),
            "admin-script",
            new Dictionary<string, string?>(),
            "10.0.0.5"
        );

        Assert.Null(result.Run);
        Assert.Empty(_runner.Runs);
        var audit = Assert.Single(AuditEvents());
        Assert.Equal(AuditActions.ScriptDenied, audit.Action);
        Assert.Equal("admin-script", audit.Target);
        Assert.Equal("10.0.0.5", audit.ClientIp);
    }

    [Fact]
    public async Task Unknown_scripts_are_refused_without_an_audit_entry()
    {
        var result = await _service.StartScriptAsync(User("HelpDesk"), "nope", new Dictionary<string, string?>(), null);

        Assert.Null(result.Run);
        Assert.Empty(AuditEvents());
    }

    [Fact]
    public async Task Terminal_is_admin_only_and_audits_the_attempt()
    {
        var result = await _service.StartCodeAsync(User("HelpDesk"), "Remove-Item C:\\ -Recurse", null, null);

        Assert.Null(result.Run);
        var audit = Assert.Single(AuditEvents());
        Assert.Equal(AuditActions.ScriptDenied, audit.Action);
        Assert.Equal("Remove-Item C:\\ -Recurse", audit.Details);
    }

    [Fact]
    public async Task Terminal_runs_are_audited_with_their_code()
    {
        var result = await _service.StartCodeAsync(User(SpecOpsAuthorizer.AdminRole), "'hello'", null, "10.0.0.9");

        Assert.NotNull(result.Run);
        var audit = Assert.Single(AuditEvents());
        Assert.Equal(AuditActions.TerminalRun, audit.Action);
        Assert.Equal("'hello'", audit.Details);
        Assert.Equal("user-1", audit.ActorId);
        _runner.Cancel(result.Run.Id);
    }

    [Fact]
    public async Task Users_only_see_their_own_runs()
    {
        var mine = (await _service.StartCodeAsync(User(SpecOpsAuthorizer.AdminRole), "'x'", null, null)).Run!;
        var someoneElse = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-2"), new Claim(ClaimTypes.Role, "HelpDesk")],
                "test"
            )
        );

        Assert.Null(await _service.FindRunAsync(someoneElse, mine.Id));
        Assert.False(await _service.CancelAsync(someoneElse, mine.Id));
    }
}
