using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
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
    // A database file, not a shared in-memory connection: runs write their history in the background while a test
    // reads, and SQLite only handles that with a connection each.
    private readonly string _database = Path.Combine(
        Path.GetTempPath(),
        $"specops-service-tests-{Guid.NewGuid():N}.db"
    );
    private readonly ServiceProvider _services;
    private readonly ScriptRunner _runner;
    private readonly RunHistory _history;
    private readonly ScriptService _service;
    private readonly CredentialVault _vault;
    private readonly DirectoryInfo _scripts = Directory.CreateTempSubdirectory("specops-service-tests-");

    public ScriptServiceTests()
    {
        _services = new ServiceCollection()
            .AddDbContext<SpecOpsDbContext>(o => o.UseSqlite($"Data Source={_database}"))
            .BuildServiceProvider();
        using (var scope = _services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>().Database.EnsureCreated();
        }

        var options = TestOptions.Create(o =>
        {
            o.ScriptsRoot = _scripts.FullName;
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
            o.Scripts.Add(
                new ScriptDefinition
                {
                    Id = "credential-script",
                    Name = "Uses a credential",
                    CategoryId = "demo",
                    Path = "credential.ps1",
                    Parameters =
                    [
                        new ScriptParameterDefinition
                        {
                            Name = "Account",
                            Type = ParameterType.Credential,
                            Options = [new SelectOption { Value = "svc" }],
                        },
                    ],
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
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        var authorizer = new SpecOpsAuthorizer(options);
        var audit = new AuditLog(scopes, NullLogger<AuditLog>.Instance);
        _vault = new CredentialVault(
            scopes,
            new EphemeralDataProtectionProvider(),
            authorizer,
            audit,
            TimeProvider.System
        );
        _service = new ScriptService(
            new ConfigurationScriptCatalog(options),
            authorizer,
            _runner,
            audit,
            _history,
            _vault
        );
    }

    public void Dispose()
    {
        _runner.Dispose();
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_database);
        _scripts.Delete(recursive: true);
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

    private static async Task<string> RunToEndAsync(ScriptRun run)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var _ in run.WatchAsync(cts.Token)) { }
        return string.Join(Environment.NewLine, run.Snapshot().Select(r => r.Text));
    }

    [Fact]
    public async Task Scripts_get_vault_credentials_without_the_password_showing_anywhere()
    {
        const string Password = "S3cret-Pa55!";
        File.WriteAllText(
            Path.Combine(_scripts.FullName, "credential.ps1"),
            "param([pscredential]$Account) \"$($Account.UserName) $($Account.GetNetworkCredential().Password.Length)\""
        );
        var admin = User(SpecOpsAuthorizer.AdminRole);
        Assert.Empty(
            await _vault.SaveAsync(
                admin,
                new CredentialInfo("svc", "Service account", @"CONTOSO\svc-specops", null, default, ""),
                Password,
                isNew: true
            )
        );

        // The only allowed credential is used without the user choosing it.
        var result = await _service.StartScriptAsync(
            User("HelpDesk"),
            "credential-script",
            new Dictionary<string, string?>(),
            null
        );

        Assert.Null(result.Error);
        var run = result.Run!;
        Assert.Contains(@"CONTOSO\svc-specops 12", await RunToEndAsync(run), StringComparison.Ordinal);
        Assert.Equal(@"CONTOSO\svc-specops", run.Parameters["Account"]);
        Assert.All(AuditEvents(), e => Assert.DoesNotContain(Password, e.Details ?? "", StringComparison.Ordinal));
        using var scope = _services.CreateScope();
        var stored = Assert.Single(scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>().Credentials);
        Assert.DoesNotContain(Password, stored.ProtectedPassword, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_cant_be_given_a_credential_they_dont_allow()
    {
        var admin = User(SpecOpsAuthorizer.AdminRole);
        await _vault.SaveAsync(
            admin,
            new CredentialInfo("other", "Other", "other", null, default, ""),
            "x",
            isNew: true
        );

        var result = await _service.StartScriptAsync(
            User("HelpDesk"),
            "credential-script",
            new Dictionary<string, string?> { ["Account"] = "other" },
            null
        );

        Assert.Null(result.Run);
        Assert.Contains("Account", result.FieldErrors.Keys);
    }

    [Fact]
    public async Task A_deleted_credential_stops_the_run_with_a_clear_message()
    {
        var result = await _service.StartScriptAsync(
            User("HelpDesk"),
            "credential-script",
            new Dictionary<string, string?>(),
            null
        );

        Assert.Null(result.Run);
        Assert.Contains("doesn't exist", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_admins_manage_credentials_and_used_ones_cant_be_deleted()
    {
        var admin = User(SpecOpsAuthorizer.AdminRole);
        var info = new CredentialInfo("svc", "Service account", "svc", null, default, "");
        Assert.NotEmpty(await _vault.SaveAsync(User("HelpDesk"), info, "x", isNew: true));
        Assert.Empty(await _vault.SaveAsync(admin, info, "x", isNew: true));
        Assert.NotEmpty(await _vault.SaveAsync(admin, info, "x", isNew: true));
        // Updating without a password keeps the current one.
        Assert.Empty(await _vault.SaveAsync(admin, info with { Name = "Renamed" }, null, isNew: false));
        Assert.Equal("Renamed", Assert.Single(await _vault.ListAsync()).Name);
        Assert.NotNull(await _vault.GetCredentialAsync("svc"));

        ScriptDefinition[] usingIt =
        [
            new()
            {
                Id = "x",
                Name = "X",
                Parameters =
                [
                    new()
                    {
                        Name = "A",
                        Type = ParameterType.Credential,
                        Options = [new() { Value = "svc" }],
                    },
                ],
            },
        ];
        ScriptDefinition[] scripts = [];
        Assert.Contains("used by", await _vault.DeleteAsync(admin, "svc", usingIt) ?? "", StringComparison.Ordinal);
        Assert.NotNull(await _vault.DeleteAsync(User("HelpDesk"), "svc", scripts));
        Assert.Null(await _vault.DeleteAsync(admin, "svc", scripts));
        Assert.Empty(await _vault.ListAsync());
        Assert.Contains(AuditEvents(), e => e.Action == AuditActions.CredentialDeleted);
    }
}
