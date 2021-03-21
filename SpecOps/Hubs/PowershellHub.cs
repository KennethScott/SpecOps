using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading.Tasks;
using SpecOps.Models;
using SpecOps.Services;
using Microsoft.Extensions.Configuration;
using SpecOps.Classes;

namespace SpecOps.Hubs
{
    /// <summary>
    /// Contains functionality for executing PowerShell scripts.
    /// *Much of the logic for executing PowerShell scripts was taken from Keith Babinec's work here:
    /// https://github.com/keithbabinec/PowerShellHostedRunspaceStarterkits
    /// </summary>
    public class PowerShellHub : Hub
    {
        private IScriptService ScriptService { get; set; }
        private readonly ILogger<PowerShellHub> Logger;
        private IHubContext<PowerShellHub> PowerShellHubContext { get; }

        private RunspacePool RsPool { get; set; }

        public PowerShellHub(IScriptService scriptService, ILogger<PowerShellHub> logger, IHubContext<PowerShellHub> powerShellHubContext)
        {
            this.ScriptService = scriptService;
            this.Logger = logger;
            this.PowerShellHubContext = powerShellHubContext;
        }

        public async Task StreamPowerShell(string scriptId, Dictionary<string, object> scriptParameters)
        {
            await StreamPowerShell(scriptId, scriptParameters, o =>
            {
                PowerShellHubContext.Clients.Client(Context.ConnectionId).SendAsync("OutputReceived", o);
            });
        }

        /// <summary>
        /// Initialize the runspace pool.
        /// </summary>
        /// <param name="runspace">Contains runspace config parameters necessary for the script</param>
        public void InitializeRunspaces(ScriptRunspace runspace)
        {
            // create the default session state.
            // session state can be used to set things like execution policy, language constraints, etc.
            var defaultSessionState = InitialSessionState.CreateDefault();
            if (!String.IsNullOrEmpty(runspace.ExecutionPolicy))
            {
                defaultSessionState.ExecutionPolicy = runspace.ExecutionPolicy.ToEnum<Microsoft.PowerShell.ExecutionPolicy>(); 
            }         

            // optionally load any modules (by name) that were supplied.
            foreach (var moduleName in runspace.Modules)
            {
                defaultSessionState.ImportPSModule(moduleName);
            }

            // use the runspace factory to create a pool of runspaces with a minimum and maximum number of runspaces to maintain.
            RsPool = RunspaceFactory.CreateRunspacePool(defaultSessionState);
            RsPool.SetMinRunspaces(runspace.Min);
            RsPool.SetMaxRunspaces(runspace.Max);

            // set the pool options for thread use.
            // we can throw away or re-use the threads depending on the usage scenario.
            RsPool.ThreadOptions = PSThreadOptions.UseNewThread;

            // open the pool. this will start by initializing the minimum number of runspaces.
            RsPool.Open();
        }

        /// <summary>
        /// Runs a PowerShell script with parameters and prints the resulting pipeline objects to the console output. 
        /// </summary>
        /// <param name="scriptId">The script file contents.</param>
        /// <param name="scriptParameters">A dictionary of parameter names and parameter values.</param>
        /// <param name="outputHandler">The outputHandler to send the script output to.</param>
        private async Task StreamPowerShell(string scriptId, Dictionary<string, object> scriptParameters, Action<object> outputHandler)
        {
            try
            {
                var script = ScriptService.GetScript(scriptId);

                outputHandler(new OutputRecord(OutputLevelName.System, "Loading script..."));
                Logger.Log(LogLevel.Information, $"{Context.User.Identity.Name} attempting to run {script.Name}");

                string scriptContents = script.GetContents();

                // Setup a custom runspace pool if configured on the script
                if (script.Runspace != null)
                {
                    InitializeRunspaces(script.Runspace);
                }

                // create a new hosted PowerShell instance potentially with a custom runspace.
                // wrap in a using statement to ensure resources are cleaned up.
                using (PowerShell ps = PowerShell.Create())
                {
                    // use the runspace pool, if it was created.
                    if (RsPool != null)
                    {
                        ps.RunspacePool = RsPool;
                    }

                    // specify the script code to run.
                    ps.AddScript(scriptContents);

                    // specify the parameters to pass into the script.
                    ps.AddParameters(scriptParameters);

                    // Subscribe to events from output
                    var output = new PSDataCollection<PSObject>();
                    output.DataAdded += (object sender, DataAddedEventArgs e) => WriteOutput<PSObject>(sender, e, outputHandler);

                    // subscribe to events from some of the streams

                    /// Handles data-added events for the error stream.
                    /// Note: Uncaught terminating errors will stop the pipeline completely.
                    /// Non-terminating errors will be written to this stream and execution will continue.
                    ps.Streams.Error.DataAdded       += (object sender, DataAddedEventArgs e) => WriteOutput<ErrorRecord>(sender, e, outputHandler);
                    ps.Streams.Warning.DataAdded     += (object sender, DataAddedEventArgs e) => WriteOutput<WarningRecord>(sender, e, outputHandler);
                    /// Handles data-added events for the information stream.
                    /// Note: Write-Host and Write-Information messages will end up in the information stream.
                    ps.Streams.Information.DataAdded += (object sender, DataAddedEventArgs e) => WriteOutput<InformationRecord>(sender, e, outputHandler);
                    ps.Streams.Progress.DataAdded    += (object sender, DataAddedEventArgs e) => WriteOutput<ProgressRecord>(sender, e, outputHandler);
                    ps.Streams.Verbose.DataAdded     += (object sender, DataAddedEventArgs e) => WriteOutput<VerboseRecord>(sender, e, outputHandler);
                    ps.Streams.Debug.DataAdded       += (object sender, DataAddedEventArgs e) => WriteOutput<DebugRecord>(sender, e, outputHandler);

                    outputHandler(new OutputRecord(OutputLevelName.System, "Beginning script execution..."));

                    await ps.InvokeAsync<PSObject, PSObject>(null, output).ConfigureAwait(false);

                    //// execute the script and await the result.
                    //var pipelineObjects = await ps.InvokeAsync().ConfigureAwait(false);
                    //foreach (var item in pipelineObjects)
                    //{
                    //    outputHandler(item.BaseObject.ToString());
                    //}

                    ////// Execute powershell command
                    ////IAsyncResult asyncResult = ps.BeginInvoke<PSObject, PSObject>(null, output);
                    ////// Wait for powershell command to finish
                    ////asyncResult.AsyncWaitHandle.WaitOne();
                    ///

                    ////ps.Invoke(null, output);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, ex, ex.Message);
                outputHandler(new OutputRecord(OutputLevelName.Error, ex.Message));
            }
            finally
            {
                outputHandler(new OutputRecord(OutputLevelName.System, "Script execution ended."));
            }
        }

        private void WriteOutput<T>(object sender, DataAddedEventArgs ea, Action<object> outputHandler)
        {
            var streamObjectsReceived = sender as PSDataCollection<T>;
            var currentStreamRecord = streamObjectsReceived[ea.Index];
            string data;
            OutputLevelName type;

            switch (currentStreamRecord)
            {
                case DebugRecord d:
                    type = OutputLevelName.Debug;
                    data = d.Message;
                    break;
                case ErrorRecord e:
                    type = OutputLevelName.Error;
                    data = e.Exception.ToString();
                    break;
                case InformationRecord i:
                    type = OutputLevelName.Info;
                    data = i.MessageData.ToString();
                    break;
                case ProgressRecord p:
                    type = OutputLevelName.Progress;
                    data = $"{p.Activity}... {p.StatusDescription} {p.PercentComplete}%";
                    break;
                case PSObject ps:
                    type = OutputLevelName.Data;
                    data = currentStreamRecord.ToString();
                    break;
                case VerboseRecord v:
                    type = OutputLevelName.Verbose;
                    data = v.Message;
                    break;
                case WarningRecord w:
                    type = OutputLevelName.Warning;
                    data = w.Message;
                    break;
                default:
                    type = OutputLevelName.Unknown;
                    data = currentStreamRecord.ToString();
                    break;
            }

            outputHandler(new OutputRecord(type, data));

        }
    }
}
