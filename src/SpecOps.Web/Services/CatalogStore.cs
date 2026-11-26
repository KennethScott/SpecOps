using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SpecOps.Core.Configuration;
using SpecOps.Data;

namespace SpecOps.Web.Services;

/// <summary>A saved catalog version (without its JSON), for the history list.</summary>
public sealed record CatalogVersionInfo(long Id, DateTimeOffset CreatedAt, string CreatedBy, string Summary);

/// <summary>
/// The live script catalog, kept in memory and backed by the CatalogVersions table. Every save adds a version; the
/// newest is live. Saving raises an options change, so everything reading <see cref="SpecOpsOptions"/> (catalog,
/// authorizer, runner) picks it up immediately, the same way a specops.json edit used to.
/// </summary>
public sealed partial class CatalogStore(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<CatalogStore> logger
) : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource _changed = new();
    private volatile Snapshot? _current;

    private sealed record Snapshot(long Version, CatalogDocument Document);

    /// <summary>The live catalog's version id.</summary>
    public long Version => Current.Version;

    /// <summary>A copy of the live catalog (safe to edit).</summary>
    public CatalogDocument GetDocument() => Current.Document.Clone();

    private Snapshot Current =>
        _current
        ?? throw new InvalidOperationException("The script catalog hasn't been loaded yet (CatalogStore.LoadAsync).");

    /// <summary>Fires when a new version is saved.</summary>
    public IChangeToken GetChangeToken() => new CancellationChangeToken(_changed.Token);

    /// <summary>
    /// At startup: loads the newest version. The very first time (no versions yet) the catalog in specops.json is
    /// imported as version 1; after that, those sections of the file are no longer used.
    /// </summary>
    public async Task LoadAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        var latest = await db.CatalogVersions.AsNoTracking().OrderByDescending(v => v.Id).FirstOrDefaultAsync();
        var fileOptions = configuration.GetSection(SpecOpsOptions.SectionName).Get<SpecOpsOptions>()
            ?? new SpecOpsOptions();
        var fromFile = CatalogDocument.From(fileOptions);

        if (latest is null)
        {
            // Never import something that can't work: fix specops.json and restart instead.
            if (new SpecOpsOptionsValidator().Validate(null, fileOptions) is { Failed: true } invalid)
            {
                throw new OptionsValidationException(
                    SpecOpsOptions.SectionName,
                    typeof(SpecOpsOptions),
                    invalid.Failures
                );
            }

            var version = new CatalogVersion
            {
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
                CreatedBy = "SpecOps",
                Summary = fromFile.IsEmpty ? "Started with an empty catalog" : "Imported from specops.json",
                Json = fromFile.ToJson(),
            };
            db.CatalogVersions.Add(version);
            await db.SaveChangesAsync();
            LogImported(fromFile.Scripts.Count, fromFile.Policies.Count);
            _current = new Snapshot(version.Id, fromFile);
            return;
        }

        _current = new Snapshot(latest.Id, CatalogDocument.Parse(latest.Json));
        if (!fromFile.IsEmpty)
        {
            LogFileCatalogIgnored();
        }
    }

    /// <summary>
    /// Saves <paramref name="document"/> as the new live version, unless someone else saved since
    /// <paramref name="expectedVersion"/> (then returns null and nothing changes). Callers validate first.
    /// </summary>
    public async Task<long?> SaveAsync(
        CatalogDocument document,
        long expectedVersion,
        string summary,
        string? userId,
        string userName
    )
    {
        ArgumentNullException.ThrowIfNull(document);
        await _writeLock.WaitAsync();
        try
        {
            if (Current.Version != expectedVersion)
            {
                return null;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
            var version = new CatalogVersion
            {
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
                CreatedById = userId,
                CreatedBy = userName.Length > 256 ? userName[..256] : userName,
                Summary = summary.Length > 512 ? summary[..512] : summary,
                Json = document.ToJson(),
            };
            db.CatalogVersions.Add(version);
            await db.SaveChangesAsync();

            _current = new Snapshot(version.Id, document.Clone());
            SignalChange();
            return version.Id;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<CatalogVersionInfo>> ListVersionsAsync(int take)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        var rows = await db
            .CatalogVersions.AsNoTracking()
            .OrderByDescending(v => v.Id)
            .Take(take)
            .Select(v => new
            {
                v.Id,
                v.CreatedAtUtc,
                v.CreatedBy,
                v.Summary,
            })
            .ToListAsync();
        return
        [
            .. rows.Select(v => new CatalogVersionInfo(
                v.Id,
                new DateTimeOffset(DateTime.SpecifyKind(v.CreatedAtUtc, DateTimeKind.Utc)),
                v.CreatedBy,
                v.Summary
            )),
        ];
    }

    public async Task<CatalogDocument?> GetVersionAsync(long id)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        var json = await db.CatalogVersions.Where(v => v.Id == id).Select(v => v.Json).FirstOrDefaultAsync();
        return json is null ? null : CatalogDocument.Parse(json);
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _changed.Dispose();
    }

    private void SignalChange()
    {
        var previous = Interlocked.Exchange(ref _changed, new CancellationTokenSource());
        // Listeners (the options monitor) run inline, so the new configuration is live when this returns.
        previous.Cancel();
        previous.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Script catalog: first run, imported {Scripts} scripts and {Policies} policies from specops.json into the database"
    )]
    private partial void LogImported(int scripts, int policies);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Script catalog: loaded from the database. Categories, Policies, RunspaceProfiles and Scripts in specops.json are no longer used (manage them on the Admin pages, or use Import there)"
    )]
    private partial void LogFileCatalogIgnored();
}

/// <summary>Feeds the database catalog into <see cref="SpecOpsOptions"/> and reloads options when it changes.</summary>
public sealed class CatalogOptionsSetup(CatalogStore store)
    : IPostConfigureOptions<SpecOpsOptions>,
        IOptionsChangeTokenSource<SpecOpsOptions>
{
    public string? Name => Options.DefaultName;

    public void PostConfigure(string? name, SpecOpsOptions options) => store.GetDocument().ApplyTo(options);

    public IChangeToken GetChangeToken() => store.GetChangeToken();
}
