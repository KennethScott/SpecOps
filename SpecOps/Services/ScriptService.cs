using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using SpecOps.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using System.Threading.Tasks;

namespace SpecOps.Services
{
    public class ScriptService : IScriptService
    {
        private readonly ScriptSettings scriptSettings;
        private readonly AppSettings appSettings;
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IAuthorizationService authorizationService;
        private readonly IMemoryCache memoryCache;

        public ScriptService(IOptionsSnapshot<ScriptSettings> scriptSettings, IHttpContextAccessor httpContextAccessor, IAuthorizationService authorizationService,
            IOptionsSnapshot<AppSettings> appSettings, IMemoryCache memoryCache)
        {
            this.scriptSettings = scriptSettings.Value;
            this.appSettings = appSettings.Value;
            this.httpContextAccessor = httpContextAccessor;
            this.authorizationService = authorizationService;
            this.memoryCache = memoryCache;
        }

        /// <summary>
        /// Get list of Categories that the user has access to
        /// </summary>
        /// <returns>List of CategoryIds</returns>
        public IEnumerable<string> GetCategories()
        {
            return AuthorizedCategories().OrderBy(s => s);
        }

        /// <summary>
        /// Get all Scripts
        /// </summary>
        /// <returns>List of Scripts</returns>
        public IEnumerable<Script> GetScripts()
        {
            return scriptSettings.Scripts;
        }

        /// <summary>
        /// Get all scripts in a particular Category
        /// </summary>
        /// <param name="category"></param>
        /// <returns>List of Scripts in the desired Category</returns>
        public IEnumerable<Script> GetScripts(string category)
        {
            return GetScripts()
                    .Where(s => s.CategoryId == category)
                    .Select(s => s)
                    .OrderBy(s => s.Name);
        }

        /// <summary>
        /// Get Script by Id
        /// </summary>
        /// <param name="Id">Unique Id from </param>
        /// <returns></returns>
        public Script GetScript(string Id)
        {
            return GetScripts().FirstOrDefault(s => s.Id == Id);
        }

        /// <summary>
        /// Get list of categories the user has access to.  These categories are strictly those from the appsettings security policies - there may 
        /// not actually be any scripts defined  in these categories in the scriptsettings file.
        /// The generated list will be cached.
        /// </summary>
        /// <returns></returns>
        private IEnumerable<string> AuthorizedCategories()
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
