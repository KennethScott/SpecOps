using Serilog;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;
using SpecOps.Web.Auth;
using SpecOps.Web.Components;
using SpecOps.Web.Infrastructure;
using SpecOps.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddSpecOpsHosting();
builder.AddSpecOpsAuthentication();
builder.Services.AddSpecOpsRateLimiting();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IScriptCatalog, ConfigurationScriptCatalog>();
builder.Services.AddSingleton<ISpecOpsAuthorizer, SpecOpsAuthorizer>();
builder.Services.AddSingleton<IScriptRunner, ScriptRunner>();
builder.Services.AddSingleton<ScriptService>();
builder.Services.AddHostedService<ScriptRunnerLifetime>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

app.UseForwardedHeaders();
if (app.Configuration["PathBase"] is { Length: > 0 } pathBase)
{
    app.UsePathBase(pathBase);
}

// CSP, HSTS (HTTPS only), frame/content-type/referrer/permissions headers.
app.UseSpecOpsSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (app.Configuration.GetValue("HttpsRedirection", true))
{
    app.UseHttpsRedirection();
}

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapAccountEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.InitializeSpecOpsAsync();
await app.RunAsync();
