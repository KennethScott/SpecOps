using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SpecOps.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Services
{
    public class AppSettingsService : IAppSettingsService
    {
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IAuthorizationService authorizationService;
        private readonly IMemoryCache memoryCache;
        private readonly AppSettings appSettings;

        public AppSettingsService(IOptionsSnapshot<ScriptSettings> scriptSettings, IHttpContextAccessor httpContextAccessor, IAuthorizationService authorizationService,
                                    IOptionsSnapshot<AppSettings> appSettings, IMemoryCache memoryCache)
        {
            this.httpContextAccessor = httpContextAccessor;
            this.authorizationService = authorizationService;
            this.memoryCache = memoryCache;
            this.appSettings = appSettings.Value;
        }

        /// <summary>
        /// Get list of categories the user has access to.  These categories are strictly those from the appsettings security policies - there may 
        /// not actually be any scripts defined  in these categories in the scriptsettings file.
        /// The generated list will be cached.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<string> AuthorizedCategories()
        {
            string cacheKey = httpContextAccessor.HttpContext.User.Identity.Name + "|Categories";

            if (!memoryCache.TryGetValue(cacheKey, out IEnumerable<string> authorizedCategories))
            {
                authorizedCategories = Enumerable.Empty<string>();

                foreach (var p in appSettings.SecurityPolicies)
                {
                    if (authorizationService.AuthorizeAsync(httpContextAccessor.HttpContext.User, p.Name).Result.Succeeded)
                        authorizedCategories = authorizedCategories.Union(p.CategoryIds);
                }

                var cacheExpiryOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = DateTime.Now.AddDays(1),
                    Priority = CacheItemPriority.High
                };
                memoryCache.Set(cacheKey, authorizedCategories, cacheExpiryOptions);
            }

            return authorizedCategories;
        }
    }
}
