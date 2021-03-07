using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using Toolbox.Hubs;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.Pages.User
{
    public class ScriptsModel : PageModel
    {
        private ILogger<ScriptsModel> logger;
        private IScriptService scriptService;

        public SelectList Categories { get; set; }


        public ScriptsModel(IScriptService scriptService, ILogger<ScriptsModel> logger, IHubContext<PowershellHub> hubContext)
        {
            this.scriptService = scriptService;
            this.logger = logger;
        }

        public async Task OnGetAsync(string rpCategoryId = "", string rpScriptId = "")
        {
            try
            {
                Categories = new SelectList(scriptService.GetCategories(), nameof(Script.CategoryId), nameof(Script.CategoryId));
            }
            catch (Exception e)
            {
                logger.Log(LogLevel.Debug, "Could not retrieve scripts");
            }
        }

        public async Task<JsonResult> OnGetScripts(string categoryId)
        {
            return new JsonResult(scriptService.GetScripts(categoryId));
        }

        public async Task<JsonResult> OnGetScript(string scriptId)
        {
            return new JsonResult(scriptService.GetScript(scriptId));
        }

    }
}
