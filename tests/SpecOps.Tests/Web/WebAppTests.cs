using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpecOps.Data;
using SpecOps.Web.Auth;
using SpecOps.Web.Infrastructure;
using SpecOps.Web.Services;

namespace SpecOps.Tests.Web;

public partial class SpecOpsWebFactory : WebApplicationFactory<Program>
{
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("specops-web-tests-");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("SpecOps:DataDirectory", _dataDirectory.FullName);
        builder.UseSetting("HttpsRedirection", "false");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
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
    }

    public static string? AntiforgeryToken(string html) =>
        AntiforgeryInputRegex().Match(html) is { Success: true } input
        && ValueRegex().Match(input.Value) is { Success: true } value
            ? WebUtility.HtmlDecode(value.Groups[1].Value)
            : null;

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*>")]
    private static partial Regex AntiforgeryInputRegex();

    [GeneratedRegex("value=\"([^\"]+)\"")]
    private static partial Regex ValueRegex();
}

public sealed class WebAppTests(SpecOpsWebFactory factory) : IClassFixture<SpecOpsWebFactory>
{
    private HttpClient Client() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Health_endpoint_is_public()
    {
        var response = await Client().GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/terminal")]
    [InlineData("/admin/users")]
    [InlineData("/scripts/demo")]
    [InlineData("/admin/logs")]
    [InlineData("/admin/logs/specops-20260101.log")]
    [InlineData("/admin/config")]
    [InlineData("/admin/config/credentials")]
    public async Task Pages_require_sign_in(string path)
    {
        var response = await Client().GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(
            "http://localhost/account/login",
            response.Headers.Location?.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Login_page_offers_dev_sign_in_in_development()
    {
        var html = await Client().GetStringAsync("/account/login", TestContext.Current.CancellationToken);

        Assert.Contains("Sign in as dev user", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dev_sign_in_gives_access_to_scripts_and_admin_pages()
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }
        );
        var ct = TestContext.Current.CancellationToken;

        var loginPage = await client.GetStringAsync("/account/login", ct);
        var token = SpecOpsWebFactory.AntiforgeryToken(loginPage);
        Assert.NotNull(token);

        var login = await client.PostAsync(
            "/account/dev-login",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["returnUrl"] = "/admin/users",
                }
            ),
            ct
        );
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/admin/users", login.Headers.Location?.ToString());

        var home = await client.GetAsync("/", ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Demo", await home.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        var users = await client.GetAsync("/admin/users", ct);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    }

    [Fact]
    public async Task Dev_login_rejects_requests_without_antiforgery_token()
    {
        var response = await Client()
            .PostAsync("/account/dev-login", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/runs", "/runs")]
    [InlineData("/", "/")]
    [InlineData(null, "~/")]
    [InlineData("https://evil.example/", "~/")]
    [InlineData("//evil.example/", "~/")]
    [InlineData("/\\evil.example/", "~/")]
    public void Return_urls_must_be_local(string? input, string expected)
    {
        Assert.Equal(expected, AccountEndpoints.SafeReturnUrl(input));
    }

    [Fact]
    public async Task Pages_send_security_headers_with_a_nonce_that_matches_the_inline_scripts()
    {
        var response = await Client().GetAsync("/account/login", TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);

        var nonce = Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains($"nonce=\"{nonce}\"", html, StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Sign_in_is_recorded_in_the_audit_log()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var ct = TestContext.Current.CancellationToken;
        var token = SpecOpsWebFactory.AntiforgeryToken(await client.GetStringAsync("/account/login", ct));

        await client.PostAsync(
            "/account/dev-login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token! }),
            ct
        );

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpecOpsDbContext>();
        Assert.Contains(await db.AuditEvents.AsNoTracking().ToListAsync(ct), e =>
            e.Action == AuditActions.SignIn && e.Actor == "Dev User"
        );
    }

    [Fact]
    public void Production_warns_about_risky_security_settings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "*",
                    ["Auth:Providers:Microsoft:ClientId"] = "id",
                    ["Auth:Providers:Microsoft:Authority"] = "https://login.microsoftonline.com/tenant/v2.0",
                }
            )
            .Build();

        var production = HostingSetup.SecurityConfigurationProblems(configuration, Environment("Production")).ToList();
        var development = HostingSetup
            .SecurityConfigurationProblems(configuration, Environment("Development"))
            .ToList();

        Assert.Contains(production, p => p.Contains("AllowedHosts", StringComparison.Ordinal));
        Assert.Contains(production, p => p.Contains("AllowedTenantIds", StringComparison.Ordinal));
        Assert.Empty(development);
    }

    private static Microsoft.Extensions.Hosting.Internal.HostingEnvironment Environment(string name) =>
        new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = name };
}
