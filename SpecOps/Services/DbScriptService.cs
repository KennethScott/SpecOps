using LiteDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SpecOps.LiteDb;
using SpecOps.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Services
{
    public class DbScriptService : IDbScriptService
    {
        private readonly ScriptSettings scriptSettings;
        private readonly IAppSettingsService appSettingsService;
        private LiteDatabase liteDb;

        public DbScriptService(IOptionsSnapshot<ScriptSettings> scriptSettings, IAppSettingsService appSettingsService, ILiteDbContext liteDbContext)
        {
            this.scriptSettings = scriptSettings.Value;
            this.appSettingsService = appSettingsService;
            this.liteDb = liteDbContext.Database;
        }

        /// <summary>
        /// Get list of Categories that the user has access to
        /// </summary>
        /// <returns>List of CategoryIds</returns>
        public IEnumerable<string> GetCategories()
        {
            return appSettingsService.AuthorizedCategories().OrderBy(s => s);
        }

        /// <summary>
        /// Get all Scripts
        /// </summary>
        /// <returns>List of Scripts</returns>
        public IEnumerable<Script> GetScripts()
        {
            var scripts = liteDb.GetCollection<Script>("Scripts").FindAll();
            return scripts;
        }

        /// <summary>
        /// Get all scripts in a particular Category
        /// </summary>
        /// <param name="category"></param>
        /// <returns>List of Scripts in the desired Category</returns>
        public IEnumerable<Script> GetScripts(string category)
        {
            return liteDb.GetCollection<Script>("Scripts")
                    .Find(s => s.CategoryId == category)
                    .Select(s => s)
                    .OrderBy(s => s.Name);
        }

        /// <summary>
        /// Get Script by Id
        /// </summary>
        /// <param name="Id">Unique Id from </param>
        /// <returns></returns>
        public Script GetScript(Guid Id)
        {
            var s = liteDb.GetCollection<Script>("Scripts")
                            .FindById(Id);
            return s;
        }

        public int Insert(Script script)
        {
            return liteDb.GetCollection<Script>("Scripts")
                .Insert(script);
        }

        public bool Update(Script script)
        {
            return liteDb.GetCollection<Script>("Scripts")
                .Update(script);
        }

        public int Delete(Guid id)
        {
            return liteDb.GetCollection<Script>("Scripts").DeleteMany(x => x.Id == id);
        }

    }
}
