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
        private readonly IConfiguration configuration;
        private IHttpContextAccessor httpContextAccessor;
        private IAuthorizationService authorizationService;

        public ScriptService(IConfiguration configuration, IHttpContextAccessor httpContextAccessor, IAuthorizationService authorizationService)
        {
            this.configuration = configuration;
            this.httpContextAccessor = httpContextAccessor;
            this.authorizationService = authorizationService;
        }

        /// <summary>
        /// Get list of Categories.  If user has Admin rights, they'll get all Categories.  If not, they'll get all Categories except 
        ///  any that start with "Admin"
        /// </summary>
        /// <returns>List of CategoryIds</returns>
        public IEnumerable<string> GetCategories()
        {
            IEnumerable<string> categories = GetScripts().GroupBy(s => s.CategoryId).Select(s => s.First().CategoryId);

            if (!UserIsAdmin())
            {
                categories = categories.Where(s => !ScriptIsAdmin(s));
            }

            return categories;
        }

        /// <summary>
        /// Get all Scripts
        /// </summary>
        /// <returns>List of Scripts</returns>
        public IEnumerable<Script> GetScripts()
        {
            // Changes to the scriptsettings.json file are detected immediately
            return configuration.GetSection(nameof(ScriptSettings)).Get<IEnumerable<Script>>();
        }

        /// <summary>
        /// Get all scripts in a particular Category
        /// </summary>
        /// <param name="category"></param>
        /// <returns>List of Scripts in the desired Category</returns>
        public IEnumerable<Script> GetScripts(string category)
        {
            return GetScripts().Where(s => s.CategoryId == category).Select(s => s);
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
        /// Check if current user has Admin rights
        /// </summary>
        /// <returns></returns>
        private bool UserIsAdmin()
        {
            return authorizationService.AuthorizeAsync(httpContextAccessor.HttpContext.User, SecurityPolicy.Admin).Result.Succeeded;
        }

        /// <summary>
        /// Check if Category starts with Admin
        /// </summary>
        /// <param name="categoryId"></param>
        /// <returns></returns>
        private bool ScriptIsAdmin(string categoryId)
        {
            return categoryId.StartsWith(SecurityPolicy.Admin);
        }
    }
}
