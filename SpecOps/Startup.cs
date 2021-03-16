using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SpecOps.Classes;
using SpecOps.Hubs;
using System.Linq;
using SpecOps.Models;
using SpecOps.Services;
using System;
using SpecOps.Middleware;

namespace SpecOps
{
    public class Startup
    {
        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Intentionally *not* injecting scriptsettings from config because it wont respect reloadOnChange
            //  Instead we'll just call Configuration.GetSection where needed because configuration is injected too..
            //////services.Configure<ScriptSettings>(options => {
            //////    options.Scripts = Configuration.GetSection(nameof(ScriptSettings)).Get<IEnumerable<Script>>();
            //////});

            services.AddHttpContextAccessor();
            services.AddAuthentication(IISDefaults.AuthenticationScheme);
            services.AddRazorPages()
                    .AddRazorRuntimeCompilation();
            services.AddSignalR();
            services.AddSingleton<IUserIdProvider, NameUserIdProvider>();
            services.AddScoped<IScriptService, ScriptService>();
            services.AddLogging();
            services.AddMemoryCache();

            // Changes to these settings require the app to be restarted so the policies can be reset
            var userGroups = Configuration.GetSection("AppSettings:UserGroups").Get<string[]>();
            var adminGroups = Configuration.GetSection("AppSettings:AdminGroups").Get<string[]>();

            var allGroups = Enumerable.Empty<string>();

            try
            {
                // combine all the groups and use that to restrict the entire site via the default policy
                allGroups = userGroups.Union(adminGroups);
            }
            catch (Exception ex)
            {
                throw new Exception("Must specify at least one value for UserGroups and one value for AdminGroups.", ex.InnerException);
            }
            
            services.AddAuthorization(options =>
            {
                // the User and Admin policies will dictate which scripts a user is allowed to run (and potentially control access to certain pages)
                options.AddPolicy(SecurityPolicy.User, policy => policy.RequireRole(userGroups));
                options.AddPolicy(SecurityPolicy.Admin, policy => policy.RequireRole(adminGroups));

                // Configure the default policy so that only members of defined groups can access this site
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .RequireRole(allGroups)
                    .Build();
            });

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            // This will make the HTTP requests log as rich logs instead of plain text.
            app.UseSerilogRequestLogging();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            //app.UseSerilogRequestLogging(options =>
            //{
            //    options.EnrichDiagnosticContext = PushSeriLogProperties;
            //});

            app.UseEndpoints(endpoints =>
            {
                // Require Authorization for all your Razor Pages
                endpoints.MapRazorPages().RequireAuthorization();
                endpoints.MapHub<PowerShellHub>("/streamPowerShell").RequireAuthorization();
            });

            loggerFactory.AddSerilog();

            app.UseErrorLogging();
        }

        public void PushSeriLogProperties(IDiagnosticContext diagnosticContext, HttpContext httpContext)
        {
            // TODO:  Why is this not working???
            diagnosticContext.Set("UserName", httpContext.User.Identity.Name);
        }
    }
}
