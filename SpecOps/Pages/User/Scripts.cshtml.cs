using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using SpecOps.Classes;
using SpecOps.Models;
using SpecOps.Services;
using System.DirectoryServices.AccountManagement;
using System.Linq;
using Microsoft.AspNetCore.Authorization;

namespace SpecOps.Pages.User
{
    // Unnecessary now that we'll restrict the entire site to members of our groups
    //[AuthorizeOr(new[] { SecurityPolicy.User, SecurityPolicy.Admin })]
    public class ScriptsModel : PageModel
    {
        private readonly ILogger<ScriptsModel> Logger;
        private readonly IScriptService ScriptService;

        public SelectList Categories { get; set; }

        public ScriptsModel(IScriptService scriptService, ILogger<ScriptsModel> logger)
        {
            this.ScriptService = scriptService;
            this.Logger = logger;
        }

        public async Task OnGetAsync(string rpCategoryId = "", string rpScriptId = "")
        {
            Categories = new SelectList(ScriptService.GetCategories());
        }

        public async Task<JsonResult> OnGetScripts(string categoryId)
        {
            return new JsonResult(ScriptService.GetScripts(categoryId));
        }

        public async Task<JsonResult> OnGetScript(string scriptId)
        {
            return new JsonResult(ScriptService.GetScript(scriptId));
        }

    }
}
