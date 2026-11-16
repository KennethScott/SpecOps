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
        return await context.NewPageAsync();
    }

    /// <summary>A fresh context, signed in as the Development admin user, on the Scripts page.</summary>
    public async Task<IPage> SignedInPageAsync()
    {
        var page = await NewPageAsync();
        await page.GotoAsync("account/login");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in as dev user" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Scripts", Exact = true })).ToBeVisibleAsync();
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
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development: dev sign-in and the sample scripts (appsettings.Development.json).
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("SpecOps:DataDirectory", dataDirectory);
            builder.UseSetting("HttpsRedirection", "false");
            // Keep the test output readable: the server's request and run logs only when something goes wrong.
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        }
    }
}
