using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SpecOps.Hubs;
using SpecOps.Middleware;
using SpecOps.Models;
using SpecOps.Services;
using System;
using System.Collections.Generic;
using System.Linq;

// Configure Logging 
ConfigureLogging();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
   .AddNegotiate();

// Add services to the container (Previously in ConfigureServices method)
var appSettingsConfig = builder.Configuration.GetSection(nameof(AppSettings));
builder.Services.Configure<AppSettings>(appSettingsConfig);
builder.Services.Configure<ScriptSettings>(options =>
{
    options.Scripts = builder.Configuration.GetSection(nameof(ScriptSettings)).Get<IEnumerable<Script>>();
});

builder.Services.AddHttpContextAccessor();

builder.Services.AddRazorPages().AddRazorRuntimeCompilation();
builder.Services.AddSignalR();
builder.Services.AddScoped<IScriptService, ScriptService>();
builder.Services.AddLogging();
builder.Services.AddMemoryCache();

var securityPolicySettings = appSettingsConfig.Get<AppSettings>().SecurityPolicies;
var allGroups = securityPolicySettings.SelectMany(p => p.Groups);

builder.Services.AddAuthorization(options =>
{
    foreach (var p in securityPolicySettings)
    {
        options.AddPolicy(p.Name, policy => policy.RequireRole(p.Groups));
    }
    options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireRole(allGroups).Build();
});

builder.Configuration.AddJsonFile("scriptSettings.json", optional: false, reloadOnChange: true);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services));

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSerilogRequestLogging();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages().RequireAuthorization();
app.MapHub<PowerShellHub>("/streamPowerShell").RequireAuthorization();
app.MapHub<PowerShellHub>("/streamPowerShellRaw").RequireAuthorization();

app.UseErrorLogging();

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal($"Failed to start {typeof(Program).Assembly.GetName().Name}", ex);
    throw;
}

void ConfigureLogging()
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", optional: true)
        .Build();

    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(configuration)
        .CreateBootstrapLogger();
}
