using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Toolbox.Models;

namespace Toolbox.Services
{
    public class ScriptService : IScriptService
    {
        private readonly IMemoryCache memoryCache;

        public ScriptService(IMemoryCache memoryCache)
        {
            this.memoryCache = memoryCache;
        }

        public IEnumerable<Script> GetCategories()
        {
            return GetScripts().GroupBy(s => s.CategoryId).Select(s => s.First());
        }

        public IEnumerable<Script> GetScripts()
        {
            string path = @"./Scripts.json";

            if (!memoryCache.TryGetValue(path, out IEnumerable<Script> scripts))
            {
                string scriptsConfig = System.IO.File.ReadAllText(path);
                scripts = JsonConvert.DeserializeObject<IEnumerable<Script>>(scriptsConfig);

                var cacheExpiryOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = DateTime.Now.AddMinutes(30),
                    Priority = CacheItemPriority.High
                };
                memoryCache.Set(path, scripts, cacheExpiryOptions);
            }

            return scripts;
        }

        public IEnumerable<Script> GetScripts(string category)
        {
            return GetScripts().Where(s => s.CategoryId == category).Select(s => s);
        }

        public Script GetScript(string Id)
        {
            return GetScripts().Where(s => s.Id == Id).FirstOrDefault();
        }
    }
}
