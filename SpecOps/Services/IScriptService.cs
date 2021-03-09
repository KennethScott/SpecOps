using System.Collections.Generic;
using Toolbox.Models;

namespace Toolbox.Services
{
    public interface IScriptService
    {
        IEnumerable<Script> GetCategories();
        IEnumerable<Script> GetScripts();
        IEnumerable<Script> GetScripts(string category);
        Script GetScript(string Id);
    }
}
