using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using SpecOps.Core.Configuration;
using SpecOps.Data;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SpecOps.Web.Infrastructure;

/// <summary>
/// Where SpecOps keeps its runtime data: database, logs, DataProtection keys and (by default) specops.json + scripts.
/// Defaults to ./data under the content root; set SpecOps:DataDirectory (e.g. /data in the container).
/// </summary>
public sealed record SpecOpsPaths(string DataDirectory, string ConfigFile)
{
    public static SpecOpsPaths Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var dataDirectory = Path.GetFullPath(
            configuration["SpecOps:DataDirectory"] ?? "data",
            environment.ContentRootPath
        );
        var configFile = Path.GetFullPath(
            configuration["SpecOps:ConfigFile"] ?? Path.Combine(dataDirectory, "specops.json"),
            environment.ContentRootPath
        );
        Directory.CreateDirectory(dataDirectory);
        return new SpecOpsPaths(dataDirectory, configFile);
    }

    /// <summary>
    /// First run: if there's no script configuration yet, copy the starter files (specops.json + scripts) from
    /// <paramref name="seedDirectory"/> next to the config file. Never overwrites anything. Used by the container
    /// image (SpecOps:SeedDirectory) so a fresh /data volume works out of the box.
    /// </summary>
    public bool SeedIfMissing(string? seedDirectory)
    {
        if (string.IsNullOrWhiteSpace(seedDirectory) || File.Exists(ConfigFile) || !Directory.Exists(seedDirectory))
        {
            return false;
        }

        var target = Path.GetDirectoryName(ConfigFile)!;
        foreach (var source in Directory.EnumerateFiles(seedDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(seedDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                File.Copy(source, destination);
            }
        }

        return File.Exists(ConfigFile);
    }
}

public static class HostingSetup
{
    public static SpecOpsPaths AddSpecOpsHosting(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var paths = SpecOpsPaths.Resolve(builder.Configuration, builder.Environment);
        paths.SeedIfMissing(builder.Configuration["SpecOps:SeedDirectory"]);
        builder.Services.AddSingleton(paths);

        // Don't advertise the server software (Kestrel adds this header after middleware runs).
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

        // Runner settings (and, until the first run imports them into the database, the script catalog) live in
        // specops.json, which reloads on change.
        builder.Configuration.AddJsonFile(paths.ConfigFile, optional: true, reloadOnChange: true);
        builder
            .Services.AddOptions<SpecOpsOptions>()
            .Bind(builder.Configuration.GetSection(SpecOpsOptions.SectionName))
            .PostConfigure(o =>
                o.ScriptsRoot = Path.GetFullPath(o.ScriptsRoot, Path.GetDirectoryName(paths.ConfigFile)!)
            );
        // Categories, policies, runspace profiles and scripts come from the database (edited on the Admin pages).
        builder.Services.AddSingleton<Services.CatalogStore>();
        builder.Services.AddSingleton<Services.CatalogOptionsSetup>();
        builder.Services.AddSingleton<IPostConfigureOptions<SpecOpsOptions>>(services =>
            services.GetRequiredService<Services.CatalogOptionsSetup>()
        );
        builder.Services.AddSingleton<IOptionsChangeTokenSource<SpecOpsOptions>>(services =>
            services.GetRequiredService<Services.CatalogOptionsSetup>()
        );
        // Validation lives in LastGoodOptionsMonitor: invalid configuration stops startup with a clear list of
        // problems (see InitializeSpecOpsAsync), while an invalid live edit is rejected and the last good
        // configuration keeps running.
        builder.Services.AddSingleton<IOptionsMonitor<SpecOpsOptions>>(
            services => new LastGoodOptionsMonitor<SpecOpsOptions>(
                new OptionsMonitor<SpecOpsOptions>(
                    services.GetRequiredService<IOptionsFactory<SpecOpsOptions>>(),
                    services.GetServices<IOptionsChangeTokenSource<SpecOpsOptions>>(),
                    new OptionsCache<SpecOpsOptions>()
                ),
                new SpecOpsOptionsValidator(),
                services.GetRequiredService<ILogger<LastGoodOptionsMonitor<SpecOpsOptions>>>()
            )
        );

        // Logging: console + rolling file in the data directory (one per day, or a new one after 100 MB, so the Logs
        // page never has to open a huge file), deleted after SpecOps:LogRetentionDays.
        var logRetention = TimeSpan.FromDays(
            Math.Max(1, builder.Configuration.GetValue("SpecOps:LogRetentionDays", 30))
        );
        builder.Services.AddSerilog(
            (services, logger) =>
                logger
                    .ReadFrom.Configuration(builder.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
                    .WriteTo.File(
                        Path.Combine(paths.DataDirectory, Services.LogFiles.FolderName, "specops-.log"),
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: 100 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: null,
                        retainedFileTimeLimit: logRetention,
                        formatProvider: System.Globalization.CultureInfo.InvariantCulture
                    )
        );

        // Database (SQLite by default).
        var connectionString = builder.Configuration.GetConnectionString("SpecOps")
            ?? $"Data Source={Path.Combine(paths.DataDirectory, "specops.db")}";
        builder.Services.AddDbContext<SpecOpsDbContext>(o => o.UseSqlite(connectionString));
        builder.Services.AddHealthChecks().AddDbContextCheck<SpecOpsDbContext>();

        // Keys survive restarts/redeploys so sign-in cookies stay valid.
        builder
            .Services.AddDataProtection()
            .SetApplicationName("SpecOps")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataDirectory, "keys")));

        // Reverse proxy support: trust X-Forwarded-* only from loopback and the configured proxies/networks.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            foreach (var proxy in builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [])
            {
                o.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [])
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        return paths;
    }

    public static async Task InitializeSpecOpsAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>().Database;
            await database.MigrateAsync();
            if (database.IsSqlite())
            {
                // Write-ahead logging: readers (e.g. the Audit page) don't block writers (audit events) and vice versa.
                // The setting is stored in the database file, so this only changes something the first time.
                await database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            }
        }

        // The script catalog (imported from specops.json the first time), before anything reads the options.
        await app.Services.GetRequiredService<Services.CatalogStore>().LoadAsync();

        // Fail fast: throws OptionsValidationException listing every problem if the configuration is invalid.
        _ = app.Services.GetRequiredService<IOptionsMonitor<SpecOpsOptions>>().CurrentValue;

        var paths = app.Services.GetRequiredService<SpecOpsPaths>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SpecOps.Startup");
        if (
            await app.Services.GetRequiredService<Services.RunHistory>().MarkInterruptedAsync()
            is > 0
                and var interrupted
        )
        {
            StartupLog.RunsInterrupted(logger, interrupted);
        }

        if (!File.Exists(paths.ConfigFile))
        {
            StartupLog.NoConfigFile(logger, paths.ConfigFile);
        }
        else
        {
            StartupLog.UsingConfigFile(logger, paths.ConfigFile, paths.DataDirectory);
        }

        foreach (var problem in app.Services.GetRequiredService<Core.Scripts.IScriptCatalog>().Validate())
        {
            StartupLog.ConfigProblem(logger, problem);
        }

        foreach (var problem in SecurityConfigurationProblems(app.Configuration, app.Environment))
        {
            StartupLog.ConfigProblem(logger, problem);
        }
    }

    /// <summary>Settings that are fine for development but risky in production.</summary>
    internal static IEnumerable<string> SecurityConfigurationProblems(
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        if (environment.IsDevelopment())
        {
            yield break;
        }

        if (configuration["AllowedHosts"] is null or "*")
        {
            yield return "AllowedHosts is '*'. Set it to the host name(s) SpecOps is served on (e.g. \"specops.contoso.com\").";
        }

        var auth = configuration.GetSection(Auth.AuthSettings.SectionName).Get<Auth.AuthSettings>();
        foreach (var (name, provider) in auth?.Providers.Where(p => p.Value.IsConfigured) ?? [])
        {
            var isEntra =
                provider.Authority?.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) == true;
            if (isEntra && provider.AllowedTenantIds.Count == 0)
            {
                yield return $"Auth provider '{name}' is Microsoft Entra ID without AllowedTenantIds; restrict it to your tenant.";
            }

            if (
                provider.TrustEmail == true
                && provider.Type == Auth.ExternalProviderType.OpenIdConnect
                && provider.AllowedTenantIds.Count == 0
            )
            {
                yield return $"Auth provider '{name}' has TrustEmail=true without AllowedTenantIds; emails may not be verified.";
            }
        }
    }
}

internal static partial class StartupLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Count} runs were still in progress when SpecOps last stopped; marked as interrupted"
    )]
    public static partial void RunsInterrupted(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No script configuration found at {ConfigFile}. Copy samples/specops.json there to get started."
    )]
    public static partial void NoConfigFile(ILogger logger, string configFile);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Scripts configured in {ConfigFile}; data in {DataDirectory}"
    )]
    public static partial void UsingConfigFile(ILogger logger, string configFile, string dataDirectory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration: {Problem}")]
    public static partial void ConfigProblem(ILogger logger, string problem);
}
