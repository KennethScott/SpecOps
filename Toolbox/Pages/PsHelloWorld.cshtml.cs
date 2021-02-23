using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Toolbox.Classes;

namespace Toolbox.Pages
{
    public class PsHelloWorldModel : PageModel
    {
        private ILogger<PsHelloWorldModel> logger;
        
        public string Output { get; set; }

        public PsHelloWorldModel(ILogger<PsHelloWorldModel> logger)
        {
            this.logger = logger;
        }

        public async Task OnGet()
        {
            List<string> output = new List<string>();

            try
            {
                var scriptContents = new StringBuilder();
                scriptContents.AppendLine("Param($StrParam, $IntParam)");
                scriptContents.AppendLine("");
                scriptContents.AppendLine("Write-Output \"Message from inside the running script\"");
                scriptContents.AppendLine("Write-Output \"This is the value from the first param: $StrParam\"");
                scriptContents.AppendLine("Write-Output \"This is the value from the second param: $IntParam\"");
                scriptContents.AppendLine("");
                scriptContents.AppendLine("Write-Output \"Here are the loaded modules in the script:\"");
                scriptContents.AppendLine("Get-Module");
                scriptContents.AppendLine("");
                scriptContents.AppendLine("# write some data to the info/warning streams");
                scriptContents.AppendLine("");
                scriptContents.AppendLine("Write-Host \"A message from write-host\"");
                scriptContents.AppendLine("Write-Information \"A message from write-information\"");
                scriptContents.AppendLine("");
                scriptContents.AppendLine("Write-Warning \"A message from write-warning\"");
                scriptContents.AppendLine("");
                scriptContents.AppendLine("# write a message to the error stream by throwing a non-terminating error");
                scriptContents.AppendLine("# note: terminating errors will stop the pipeline.");
                scriptContents.AppendLine("Get-ChildItem -Directory \"folder-doesnt-exist\"");
                scriptContents.AppendLine("");

                var scriptParameters = new Dictionary<string, object>()
                {
                    { "StrParam", "Hello from script" },
                    { "IntParam", 7 }
                };

                output.Add("Initializing runspace pool.");

                // The 'Az' module (bundle) is the Windows Azure PowerShell module that works on both PS 5.1 and PS Core.
                // For this example to work, the Az module should already be installed.

                //var modulesToLoad = new string[] { "Az.Accounts", "Az.Compute" };
                var modulesToLoad = new string[] { };

                var hosted = new HostedRunspace();
                hosted.InitializeRunspaces(2, 10, modulesToLoad);

                output.Add("Calling RunScript()");
                await hosted.RunScript(scriptContents.ToString(), scriptParameters);

                output.Add(hosted.ExecutionOutput.ToString());

                output.Add("Script execution completed.");

            }
            catch (Exception ex)
            {
                output.Add("Error executing script." + Environment.NewLine + ex.ToString());
                logger.Log(LogLevel.Error, ex, "Error executing script");
            }
            finally
            {
                Output = string.Join(Environment.NewLine, output);
            }

        }
    }
}
