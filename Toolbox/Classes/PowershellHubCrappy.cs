using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading.Tasks;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using System.Collections.ObjectModel;

namespace Toolbox.Classes
{
    /// <summary>
    /// Contains functionality for executing PowerShell scripts.
    /// </summary>
    public class PowershellHubCrappy : Hub
    {
        public IHubContext<PowershellHubCrappy> powershellHubContext { get; }

        public PowershellHubCrappy(IHubContext<PowershellHubCrappy> powershellHubContext)
        {
            this.powershellHubContext = powershellHubContext;
        }

        public async void StreamPowershell(string scriptContents, Dictionary<string, object> scriptParameters)
        {
            await StreamPowershell(scriptContents, scriptParameters, o =>
            {
                powershellHubContext.Clients.All.SendAsync("OutputReceived", o);
            });
        }


        /// <summary>
        /// Runs a PowerShell script with parameters and prints the resulting pipeline objects to the console output. 
        /// </summary>
        /// <param name="scriptContents">The script file contents.</param>
        /// <param name="scriptParameters">A dictionary of parameter names and parameter values.</param>

        private async Task StreamPowershell(string scriptContents, Dictionary<string, object> scriptParameters, Action<string> outputHandler)
        {
            //string pscommand = Server.MapPath(target);
            PowerShell shell = PowerShell.Create();

            // Setup powershell command and execute
            shell.AddScript(scriptContents);
            //    .AddParameter("target", target); //Add PowerShell script parameters here if you need

            // Sleep a few secs to allow enough time for Results window to open and establish connection to OutputHub
            // Without this, output may not show
            //System.Threading.Thread.Sleep(3000);

            outputHandler("Executing...\n");
            string prevmsg = "";
            string msg = "";

            // Collect powershell OUTPUT and send to OutputHub
            var output = new PSDataCollection<PSObject>();

            output.DataAdded += delegate (object sender, DataAddedEventArgs e)
            {
                msg = output[e.Index].ToString();

                if (msg != prevmsg)
                {
                    outputHandler(msg + "\n");
                }
                else
                {
                    outputHandler(".\n");
                }
                prevmsg = msg;
                var psoutput = (PSDataCollection<PSObject>)sender;
                Collection<PSObject> results = psoutput.ReadAll();
            };

            prevmsg = "";
            // Collect powershell PROGRESS output and send to OutHub
            shell.Streams.Progress.DataAdded += delegate (object sender, DataAddedEventArgs e)
            {
                msg = shell.Streams.Progress[e.Index].Activity.ToString();
                if (msg != prevmsg)
                {
                    outputHandler(msg + "\n");
                }
                else
                {
                    outputHandler(".\n");
                }
                prevmsg = msg;
                var psprogress = (PSDataCollection<ProgressRecord>)sender;
                Collection<ProgressRecord> results = psprogress.ReadAll();
            };

            prevmsg = "";
            // Collect powershell WARNING output and send to OutHub
            shell.Streams.Warning.DataAdded += delegate (object sender, DataAddedEventArgs e)
            {
                msg = shell.Streams.Warning[e.Index].ToString();
                if (msg != prevmsg)
                {
                    outputHandler(msg + "\n");
                }
                else
                {
                    outputHandler(".\n");
                }
                prevmsg = msg;
                var pswarning = (PSDataCollection<WarningRecord>)sender;
                Collection<WarningRecord> results = pswarning.ReadAll();
            };

            prevmsg = "";
            // Collect powershell ERROR output and send to OutHub
            shell.Streams.Error.DataAdded += delegate (object sender, DataAddedEventArgs e)
            {
                msg = shell.Streams.Error[e.Index].ToString();
                if (msg != prevmsg)
                {
                    outputHandler(msg + "\n");
                }
                else
                {
                    outputHandler(".\n");
                }
                prevmsg = msg;
                var pserror = (PSDataCollection<ErrorRecord>)sender;
                Collection<ErrorRecord> results = pserror.ReadAll();
            };

            // Execute powershell command
            //IAsyncResult asyncResult = shell.BeginInvoke<PSObject, PSObject>(null, output);

            // Wait for powershell command to finish
            //asyncResult.AsyncWaitHandle.WaitOne();

            // var results2 = shell.Invoke();
            // hub.Clients.Group(hubGroup).addNewMessageToPage(results2);

            var pipelineObjects = await shell.InvokeAsync().ConfigureAwait(false);
            foreach (var item in pipelineObjects)
            {
                outputHandler(item.BaseObject.ToString() + "\n");
            }

            outputHandler("EXECUTION COMPLETE. Check above results for any errors.");

        }
    }
}
