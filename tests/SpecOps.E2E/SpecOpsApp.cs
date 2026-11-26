using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

[assembly: AssemblyFixture(typeof(SpecOps.E2E.SpecOpsApp))]

namespace SpecOps.E2E;

/// <summary>
/// One SpecOps server (Kestrel on a free port, Development environment, sample scripts, dev sign-in) and one
/// Chromium instance, shared by every test in the assembly. Each test gets its own browser context.
/// </summary>
public sealed class SpecOpsApp : IAsyncLifetime
{
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("specops-e2e-");
    private readonly Factory _factory;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public SpecOpsApp() => _factory = new Factory(_dataDirectory.FullName);

    /// <summary>The server's address, e.g. http://127.0.0.1:51234/.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        BaseAddress = _factory.ClientOptions.BaseAddress;

        InstallBrowser();
        // Runs start PowerShell, which can take a few seconds on a cold machine (CI).
        SetDefaultExpectTimeout(15_000);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(
            new() { Headless = Environment.GetEnvironmentVariable("SPECOPS_E2E_HEADED") is null }
        );
    }

    /// <summary>A fresh browser context (own cookies and storage) with a page on the app.</summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await _browser!.NewContextAsync(
            new()
            {
                BaseURL = BaseAddress.ToString(),
                ViewportSize = new() { Width = 1400, Height = 1000 },
            }
        );
        context.SetDefaultTimeout(15_000);
        var page = await context.NewPageAsync();

        // SPECOPS_E2E_CPU_THROTTLE=6 slows the browser's CPU down that many times (Chromium only), to reproduce
        // timing problems that only show up on slow machines such as CI runners.
        if (
            double.TryParse(
                Environment.GetEnvironmentVariable("SPECOPS_E2E_CPU_THROTTLE"),
                System.Globalization.CultureInfo.InvariantCulture,
                out var rate
            )
            && rate > 1
        )
        {
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Emulation.setCPUThrottlingRate", new Dictionary<string, object> { ["rate"] = rate });
        }

        return page;
    }

    /// <summary>
    /// Runs <paramref name="test"/>; if it fails, the failure message also gets the browser console and the server's
    /// warnings and errors, so a failure on CI can be diagnosed from the test output alone.
    /// </summary>
    public async Task WithDiagnosticsAsync(IPage page, Func<Task> test)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(test);
        var console = new List<string>();
        page.Console += (_, message) => console.Add($"[{message.Type}] {message.Text}");
        page.PageError += (_, error) => console.Add($"[page error] {error}");
        try
        {
            await test();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"""
                {ex.Message}
                --- Page: {page.Url}
                --- Browser console:
                {string.Join(Environment.NewLine, console.TakeLast(20))}
                --- Server warnings and errors:
                {ServerLog()}
                """,
                ex
            );
        }
    }

    /// <summary>The end of the server's log file (Serilog, warnings and up in these tests).</summary>
    private string ServerLog()
    {
        var logs = new DirectoryInfo(Path.Combine(_dataDirectory.FullName, "logs"));
        var file = logs.Exists ? logs.GetFiles("*.log").MaxBy(f => f.LastWriteTimeUtc) : null;
        if (file is null)
        {
            return "(no log file)";
        }

        using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split(Environment.NewLine);
        return string.Join(Environment.NewLine, lines.TakeLast(30));
    }

    /// <summary>
    /// Waits until the page is driven by the live connection (MainLayout sets data-interactive). Before that, the
    /// page is the prerendered HTML: controls are disabled and clicks may be lost.
    /// </summary>
    public static Task WaitForInteractiveAsync(IPage page) =>
        Expect(page.Locator("[data-interactive=true]")).ToBeAttachedAsync(new() { Timeout = 30_000 });

    /// <summary>A fresh context, signed in as the Development admin user, on the Scripts page.</summary>
    public async Task<IPage> SignedInPageAsync()
    {
        var page = await NewPageAsync();
        await page.GotoAsync("account/login");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in as dev user" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Scripts", Exact = true })).ToBeVisibleAsync();
        await WaitForInteractiveAsync(page);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            _dataDirectory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Log files may still be held open briefly; temp folders get cleaned up eventually.
        }
    }

    /// <summary>
    /// Downloads Playwright's Chromium if it isn't there yet (a no-op afterwards). On Linux the browser's system
    /// libraries must be installed separately: playwright.ps1 install --with-deps chromium (see docs/testing.md).
    /// </summary>
    private static void InstallBrowser()
    {
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Installing Playwright's Chromium failed (exit code {exitCode}).");
        }
    }

    private sealed class Factory(string dataDirectory) : WebApplicationFactory<Program>
    {
        private static string SamplesDirectory()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SpecOps.slnx")))
                {
                    return Path.Combine(dir.FullName, "samples");
                }
            }

            throw new DirectoryNotFoundException(
                "Couldn't find the repository root (SpecOps.slnx) above the test output."
            );
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development: dev sign-in and the sample scripts (appsettings.Development.json).
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("SpecOps:DataDirectory", dataDirectory);
            // A copy of the samples (specops.json + scripts) in the data directory, so tests that edit scripts
            // never touch the repo.
            builder.UseSetting("SpecOps:ConfigFile", Path.Combine(dataDirectory, "specops.json"));
            builder.UseSetting("SpecOps:SeedDirectory", SamplesDirectory());
            builder.UseSetting("HttpsRedirection", "false");
            // Keep the test output readable: the server's request and run logs only when something goes wrong.
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        }
    }
}
