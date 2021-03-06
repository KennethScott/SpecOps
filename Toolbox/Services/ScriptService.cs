using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Toolbox.Models;

namespace Toolbox.Services
{
    public class ScriptService : IScriptService
    {
        public IEnumerable<Script> GetCategories()
        {
            return GetScripts().GroupBy(s => s.CategoryId).Select(s => s.First());
        }

        public IEnumerable<Script> GetScripts()
        {
            string path = @"./Scripts.json";

            string scriptsConfig = System.IO.File.ReadAllText(path);

            return JsonConvert.DeserializeObject<List<Script>>(scriptsConfig);
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
