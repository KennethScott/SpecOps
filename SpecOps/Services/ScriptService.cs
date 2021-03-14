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
    //TODO: Leaving MemoryCache in case we want to cache the 
    public class ScriptService : IScriptService
    {
        private readonly IConfiguration configuration;
        private IHttpContextAccessor httpContextAccessor;
        private IAuthorizationService authorizationService;

        public ScriptService(IConfiguration configuration, IHttpContextAccessor httpContextAccessor, IAuthorizationService authorizationService)
        {
            this.configuration = configuration;
            this.httpContextAccessor = httpContextAccessor;
            this.authorizationService = authorizationService;
        }

        public IEnumerable<string> GetCategories()
        {
            IEnumerable<string> categories = GetScripts().GroupBy(s => s.CategoryId).Select(s => s.First().CategoryId);

            if (!UserIsAdmin())
            {
                categories = categories.Where(s => !ScriptIsAdmin(s));
            }

            return categories;
        }

        public IEnumerable<Script> GetScripts()
        {
            return configuration.GetSection(nameof(ScriptSettings)).Get<IEnumerable<Script>>();
        }

        public IEnumerable<Script> GetScripts(string category)
        {
            return GetScripts().Where(s => s.CategoryId == category).Select(s => s);
        }

        public Script GetScript(string Id)
        {
            return GetScripts().FirstOrDefault(s => s.Id == Id);
        }

        private bool UserIsAdmin()
        {
            return authorizationService.AuthorizeAsync(httpContextAccessor.HttpContext.User, "Admin").Result.Succeeded;
        }
        private bool ScriptIsAdmin(string categoryId)
        {
            return categoryId.StartsWith("Admin");
        }
    }
}
