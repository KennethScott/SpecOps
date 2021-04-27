using SpecOps.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Services
{
    public interface IDbScriptService
    {
        IEnumerable<string> GetCategories();
        IEnumerable<Script> GetScripts();
        IEnumerable<Script> GetScripts(string category);
        Script GetScript(Guid Id);

        public int Insert(Script script);

        public bool Update(Script script);

        public int Delete(Guid id);

    }
}
