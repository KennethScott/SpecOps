using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using SpecOps.Hubs;
using SpecOps.Models;
using SpecOps.Services;

namespace SpecOps.Pages.User
{
    public class ScriptsModel : PageModel
    {
        private readonly ILogger<ScriptsModel> Logger;
        private readonly IScriptService ScriptService;

        public SelectList Categories { get; set; }

        public ScriptsModel(IScriptService scriptService, ILogger<ScriptsModel> logger, IHubContext<PowerShellHub> hubContext)
        {
            this.ScriptService = scriptService;
            this.Logger = logger;
        }

        public async Task OnGetAsync(string rpCategoryId = "", string rpScriptId = "")
        {
            try
            {
                Categories = new SelectList(ScriptService.GetCategories(), nameof(Script.CategoryId), nameof(Script.CategoryId));
                Logger.Log(LogLevel.Information, "Got Categories", Categories);
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, e, "Could not retrieve scripts");
            }
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
