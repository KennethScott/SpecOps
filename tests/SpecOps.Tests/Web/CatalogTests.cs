using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;
using SpecOps.Core.Hosting;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;
using SpecOps.Data;
using SpecOps.Web.Services;

namespace SpecOps.Tests.Web;

/// <summary>The script catalog in the database: first-run import, saving, validation, history and live reload.</summary>
public sealed class CatalogTests : IDisposable
{
    private const string FileCatalog = """
        {
          // Comments and trailing commas are fine, as in specops.json.
          "SpecOps": {
            "Runner": { "MaxConcurrentRuns": 3 },
            "Categories": [ { "Id": "demo", "Name": "Demos" } ],
            "Policies": [ { "Name": "Help desk", "Roles": ["HelpDesk"], "Categories": ["demo"] } ],
            "Scripts": [
              { "Id": "hello", "Name": "Hello", "CategoryId": "demo", "Path": "Hello.ps1", "Platforms": "Windows, Linux",
                "Parameters": [ { "Name": "Who", "Type": "Select", "Options": [ { "Value": "a", "Label": "A" } ] } ] },
            ],
          }
        }
        """;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;
    private readonly DirectoryInfo _scripts = Directory.CreateTempSubdirectory("specops-catalog-tests-");

    public CatalogTests()
    {
        _connection.Open();
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(FileCatalog)))
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(TimeProvider.System)
            .AddDbContext<SpecOpsDbContext>(o => o.UseSqlite(_connection));
        services
            .AddOptions<SpecOpsOptions>()
            .Bind(configuration.GetSection(SpecOpsOptions.SectionName))
            .PostConfigure(o => o.ScriptsRoot = _scripts.FullName);
        services.AddSingleton<CatalogStore>();
        services.AddSingleton<CatalogOptionsSetup>();
        services.AddSingleton<IPostConfigureOptions<SpecOpsOptions>>(s => s.GetRequiredService<CatalogOptionsSetup>());
        services.AddSingleton<IOptionsChangeTokenSource<SpecOpsOptions>>(s =>
            s.GetRequiredService<CatalogOptionsSetup>()
        );
        services.AddSingleton<IScriptCatalog, ConfigurationScriptCatalog>();
        services.AddSingleton<ISpecOpsAuthorizer, SpecOpsAuthorizer>();
        services.AddSingleton<AuditLog>();
        services.AddSingleton<CatalogAdmin>();
        services.AddSingleton<ScriptFiles>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
        _scripts.Delete(recursive: true);
    }

    private T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    private static ClaimsPrincipal Admin() =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                    new Claim(ClaimTypes.Name, "admin@contoso.com"),
                    new Claim(ClaimTypes.Role, SpecOpsAuthorizer.AdminRole),
                ],
                "test"
            )
        );

    private async Task<SpecOpsOptions> LoadAsync()
    {
        await Get<CatalogStore>().LoadAsync();
        return Get<IOptionsMonitor<SpecOpsOptions>>().CurrentValue;
    }

    [Fact]
    public async Task First_run_imports_the_catalog_from_specops_json()
    {
        var options = await LoadAsync();

        var script = Assert.Single(options.Scripts);
        Assert.Equal("Hello", script.Name);
        Assert.Equal(ScriptPlatforms.Windows | ScriptPlatforms.Linux, script.Platforms);
        Assert.Equal("A", Assert.Single(Assert.Single(script.Parameters).Options).Label);
        Assert.Equal(3, options.Runner.MaxConcurrentRuns);
        var version = Assert.Single(await Get<CatalogAdmin>().ListVersionsAsync());
        Assert.Equal("Imported from specops.json", version.Summary);
    }

    [Fact]
    public async Task Saved_changes_apply_immediately_and_are_audited()
    {
        await LoadAsync();
        var admin = Get<CatalogAdmin>();

        var result = await admin.ChangeAsync(Admin(), "Renamed", doc =>
        {
            doc.Scripts[0].Name = "Hello there";
            doc.Policies[0].Categories.Add("*");
        });

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal("Hello there", Get<IScriptCatalog>().FindScript("hello")!.Name);
        Assert.True(
            Get<ISpecOpsAuthorizer>()
                .GetAccess(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "HelpDesk")], "t")))
                .AllCategories
        );
        using var scope = _services.CreateScope();
        var audit = Assert.Single(scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>().AuditEvents);
        Assert.Equal(AuditActions.ConfigChanged, audit.Action);
        Assert.Equal("Renamed", audit.Details);
    }

    [Fact]
    public async Task Invalid_changes_are_refused_and_nothing_changes()
    {
        await LoadAsync();
        var admin = Get<CatalogAdmin>();

        var result = await admin.ChangeAsync(Admin(), "Bad", doc => doc.Scripts.Add(
                new ScriptDefinition
                {
                    Id = "hello",
                    Name = "Copy",
                    CategoryId = "demo",
                    Path = "x.ps1",
                }
            ));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate script id", StringComparison.Ordinal));
        Assert.Single(Get<IScriptCatalog>().Scripts);
        Assert.Single(await admin.ListVersionsAsync());
    }

    [Fact]
    public async Task Saving_over_someone_elses_change_is_refused()
    {
        await LoadAsync();
        var admin = Get<CatalogAdmin>();
        var (mine, version) = admin.Load();
        await admin.ChangeAsync(Admin(), "Theirs", doc => doc.Categories[0].Name = "Theirs");

        mine.Categories[0].Name = "Mine";
        var result = await admin.SaveAsync(Admin(), mine, version, "Mine");

        Assert.False(result.Succeeded);
        Assert.Equal("Theirs", Get<IScriptCatalog>().Categories[0].Name);
    }

    [Fact]
    public async Task Only_admins_can_change_the_catalog()
    {
        await LoadAsync();
        var helpDesk = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "HelpDesk")], "t"));

        var result = await Get<CatalogAdmin>().ChangeAsync(helpDesk, "Nope", doc => doc.Scripts.Clear());

        Assert.False(result.Succeeded);
        Assert.Single(Get<IScriptCatalog>().Scripts);
    }

    [Fact]
    public async Task Earlier_versions_can_be_restored_and_exports_import_again()
    {
        await LoadAsync();
        var admin = Get<CatalogAdmin>();
        var original = admin.Version;
        var exported = (await admin.ExportVersionAsync(original))!;
        await admin.ChangeAsync(Admin(), "Remove all", doc => doc.Scripts.Clear());
        Assert.Empty(Get<IScriptCatalog>().Scripts);

        Assert.True((await admin.RestoreAsync(Admin(), original)).Succeeded);
        Assert.Single(Get<IScriptCatalog>().Scripts);

        await admin.ChangeAsync(Admin(), "Remove all", doc => doc.Scripts.Clear());
        Assert.True((await admin.ImportAsync(Admin(), exported, "specops.json")).Succeeded);
        Assert.Equal("Hello", Assert.Single(Get<IScriptCatalog>().Scripts).Name);
        Assert.Equal(5, (await admin.ListVersionsAsync()).Count);
    }

    [Fact]
    public async Task Later_starts_use_the_database_not_the_file()
    {
        await LoadAsync();
        await Get<CatalogAdmin>().ChangeAsync(Admin(), "Rename", doc => doc.Scripts[0].Name = "From the database");

        // A fresh store (a restart) reads the newest version back.
        using var restarted = new CatalogStore(
            Get<IServiceScopeFactory>(),
            Get<IConfiguration>(),
            TimeProvider.System,
            NullLogger<CatalogStore>.Instance
        );
        await restarted.LoadAsync();

        Assert.Equal("From the database", restarted.GetDocument().Scripts[0].Name);
    }

    [Theory]
    [InlineData("Hello.ps1", true)]
    [InlineData("tools/Module.psm1", true)]
    [InlineData("../outside.ps1", false)]
    [InlineData("notes.txt", false)]
    [InlineData("", false)]
    public async Task Script_files_must_be_powershell_inside_the_scripts_folder(string path, bool allowed)
    {
        await LoadAsync();
        Assert.Equal(allowed, Get<ScriptFiles>().Resolve(path) is not null);
        Assert.Null(Get<ScriptFiles>().Resolve(Path.Combine(_scripts.FullName, "Hello.ps1")));
    }

    [Fact]
    public async Task Script_files_are_saved_and_audited()
    {
        await LoadAsync();
        var files = Get<ScriptFiles>();

        Assert.Null(await files.SaveAsync(Admin(), "sub/New.ps1", "Write-Output 'hi'"));
        Assert.Null(await files.SaveAsync(Admin(), "sub/New.ps1", "Write-Output 'hello'"));
        Assert.NotNull(await files.SaveAsync(Admin(), "../Escape.ps1", "x"));

        Assert.Equal("Write-Output 'hello'", await files.ReadAsync("sub/New.ps1"));
        Assert.Equal(["sub/New.ps1"], files.List());
        using var scope = _services.CreateScope();
        Assert.Equal(
            [AuditActions.ScriptFileCreated, AuditActions.ScriptFileSaved],
            scope
                .ServiceProvider.GetRequiredService<SpecOpsDbContext>()
                .AuditEvents.OrderBy(e => e.Id)
                .Select(e => e.Action)
        );
    }

    [Fact]
    public async Task Uploads_are_stored_byte_for_byte_and_can_be_deleted()
    {
        await LoadAsync();
        var files = Get<ScriptFiles>();
        // No BOM, CRLF line endings and a signature block: an upload must keep all of it, or a signature breaks.
        var signed = System.Text.Encoding.UTF8.GetBytes(
            $"Write-Output 'signed'\r\n\r\n{ScriptFiles.SignatureMarker}\r\n# MIIb...\r\n# SIG # End signature block\r\n"
        );

        Assert.Null(await files.UploadAsync(Admin(), "lib/Signed.ps1", new MemoryStream(signed)));
        Assert.NotNull(await files.UploadAsync(Admin(), "../Escape.ps1", new MemoryStream(signed)));
        Assert.NotNull(await files.UploadAsync(Admin(), "notes.txt", new MemoryStream(signed)));

        Assert.Equal(
            signed,
            await File.ReadAllBytesAsync(
                Path.Combine(_scripts.FullName, "lib", "Signed.ps1"),
                TestContext.Current.CancellationToken
            )
        );
        var listed = Assert.Single(files.ListDetails());
        Assert.Equal(("lib/Signed.ps1", signed.LongLength), (listed.Path, listed.Size));

        Assert.Null(await files.DeleteAsync(Admin(), "lib/Signed.ps1"));
        Assert.NotNull(await files.DeleteAsync(Admin(), "lib/Signed.ps1"));
        Assert.Empty(files.List());
        using var scope = _services.CreateScope();
        Assert.Equal(
            [AuditActions.ScriptFileCreated, AuditActions.ScriptFileDeleted],
            scope
                .ServiceProvider.GetRequiredService<SpecOpsDbContext>()
                .AuditEvents.OrderBy(e => e.Id)
                .Select(e => e.Action)
        );
    }

    [Fact]
    public void Catalog_json_round_trips_without_computed_properties()
    {
        var document = CatalogDocument.Parse(FileCatalog);

        var json = document.ToJson();

        Assert.DoesNotContain("DisplayName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSupportedOnHost", json, StringComparison.Ordinal);
        Assert.Contains("\"Platforms\": \"Windows, Linux\"", json, StringComparison.Ordinal);
        Assert.Equal(json, CatalogDocument.Parse(json).ToJson());
    }
}
