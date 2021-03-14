using System.Collections.Generic;
using SpecOps.Models;

namespace SpecOps.Services
{
    public interface IScriptService
    {
        IEnumerable<string> GetCategories();
        IEnumerable<Script> GetScripts();
        IEnumerable<Script> GetScripts(string category);
        Script GetScript(string Id);
    }
}
