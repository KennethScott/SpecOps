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
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace SpecOps.Pages.User
{
    // Unnecessary now that we'll restrict the entire site to members of our groups
    //[AuthorizeOr(new[] { SecurityPolicy.User, SecurityPolicy.Admin })]
    public class ScriptsModel : PageModel
    {
        private readonly ILogger<ScriptsModel> Logger;
        private readonly IScriptService ScriptService;
        private readonly IConfiguration Configuration;

        public SelectList Categories { get; set; }
        public string OutputLevelStyles { get; set; }

        public ScriptsModel(IScriptService scriptService, ILogger<ScriptsModel> logger, IConfiguration configuration)
        {
            this.ScriptService = scriptService;
            this.Logger = logger;
            this.Configuration = configuration;
        }

        // TODO: Figure out how to really make these async...
        public async Task OnGetAsync(string rpCategoryId = "", string rpScriptId = "")
        {
            var styles = Configuration.GetSection("AppSettings:OutputStyles").Get<OutputLevelStyles>();
            OutputLevelStyles = JsonSerializer.Serialize(styles);

            Categories = new SelectList(ScriptService.GetCategories());
        }

        public async Task<JsonResult> OnGetScriptsAsync(string categoryId)
        {
            return new JsonResult(ScriptService.GetScripts(categoryId));
        }

        public async Task<JsonResult> OnGetScriptAsync(string scriptId)
        {
            return new JsonResult(ScriptService.GetScript(scriptId));
        }

    }
}
