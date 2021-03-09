using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using SpecOps.Models;

namespace SpecOps.Services
{
    //TODO: Leaving MemoryCache in case we want to cache the 
    public class ScriptService : IScriptService
    {
        private readonly IMemoryCache memoryCache;
        private readonly IConfiguration configuration;

        public ScriptService(IMemoryCache memoryCache, IConfiguration configuration)
        {
            this.memoryCache = memoryCache;
            this.configuration = configuration;
        }

        public IEnumerable<Script> GetCategories()
        {
            return GetScripts().GroupBy(s => s.CategoryId).Select(s => s.First());
        }

        public IEnumerable<Script> GetScripts()
        {
            //string path = @"./Scripts.json";

            //if (!memoryCache.TryGetValue(path, out IEnumerable<Script> scripts))
            //{
            //    string scriptsConfig = System.IO.File.ReadAllText(path);
            //    scripts = JsonConvert.DeserializeObject<IEnumerable<Script>>(scriptsConfig);

            //    var cacheExpiryOptions = new MemoryCacheEntryOptions
            //    {
            //        AbsoluteExpiration = DateTime.Now.AddMinutes(30),
            //        Priority = CacheItemPriority.High
            //    };
            //    memoryCache.Set(path, scripts, cacheExpiryOptions);
            //}

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
    }
}
