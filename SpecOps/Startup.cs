using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Toolbox.Classes;
using Toolbox.Hubs;
using Toolbox.Services;

namespace Toolbox
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

            //services.AddAuthentication(IISDefaults.AuthenticationScheme);
            services.AddRazorPages()
                    .AddRazorRuntimeCompilation();
            services.AddSignalR();
            services.AddSingleton<IUserIdProvider, NameUserIdProvider>();
            services.AddScoped<IScriptService, ScriptService>();
            services.AddLogging();
            services.AddMemoryCache();
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
                endpoints.MapRazorPages();
                endpoints.MapHub<ProcessHub>("/stream");
                endpoints.MapHub<PowerShellHub>("/streamPowerShell");
            });

            loggerFactory.AddSerilog();
        }

        public void PushSeriLogProperties(IDiagnosticContext diagnosticContext, HttpContext httpContext)
        {
            // TODO:  Why is this not working???
            diagnosticContext.Set("UserName", httpContext.User.Identity.Name);
        }
    }
}
