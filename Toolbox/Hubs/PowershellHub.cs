using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading.Tasks;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.Hubs
{
    /// <summary>
    /// Contains functionality for executing PowerShell scripts.
    /// </summary>
    [Authorize]
    public class PowerShellHub : Hub
    {
        private IScriptService ScriptService { get; set; }

        private readonly ILogger<PowerShellHub> Logger;

        private RunspacePool RsPool { get; set; }

        private IHubContext<PowerShellHub> PowerShellHubContext { get; }

        public PowerShellHub(IScriptService scriptService, ILogger<PowerShellHub> logger, IHubContext<PowerShellHub> powerShellHubContext)
        {
            this.ScriptService = scriptService;
            this.Logger = logger;
            this.PowerShellHubContext = powerShellHubContext;
        }

        public async Task StreamPowerShell(string scriptId, Dictionary<string, object> scriptParameters)
        {
            var user = Context.User.Identity.Name;

            await StreamPowerShell(scriptId, scriptParameters, o =>
            {
                PowerShellHubContext.Clients.User(user).SendAsync("OutputReceived", o);
            });
        }

        /// <summary>
        /// Initialize the runspace pool.
        /// </summary>
        /// <param name="minRunspaces">Minimum number of runspaces to initialize</param>
        /// <param name="maxRunspaces">Maximum number of runspaces to initialize</param>
        /// <param name="modulesToLoad">Array of powershell modules to load</param>
        public void InitializeRunspaces(int minRunspaces, int maxRunspaces, string[] modulesToLoad)
        {
            // create the default session state.
            // session state can be used to set things like execution policy, language constraints, etc.
            // optionally load any modules (by name) that were supplied.

            var defaultSessionState = InitialSessionState.CreateDefault();
            defaultSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Unrestricted;

            foreach (var moduleName in modulesToLoad)
            {
                defaultSessionState.ImportPSModule(moduleName);
            }

            // use the runspace factory to create a pool of runspaces
            // with a minimum and maximum number of runspaces to maintain.

            RsPool = RunspaceFactory.CreateRunspacePool(defaultSessionState);
            RsPool.SetMinRunspaces(minRunspaces);
            RsPool.SetMaxRunspaces(maxRunspaces);

            // set the pool options for thread use.
            // we can throw away or re-use the threads depending on the usage scenario.

            RsPool.ThreadOptions = PSThreadOptions.UseNewThread;

            // open the pool. 
            // this will start by initializing the minimum number of runspaces.

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

                Logger.Log(LogLevel.Information, $"{Context.User.Identity.Name} attempting to run script.", script);

                string scriptContents = script.GetContents();

                //if (RsPool == null)
                //{
                //    InitializeRunspaces(2, 10, Array.Empty<string>());
                //    //throw new ApplicationException("Runspace Pool must be initialized before calling RunScript().");
                //}

                // create a new hosted PowerShell instance using a custom runspace.
                // wrap in a using statement to ensure resources are cleaned up.

                using (PowerShell ps = PowerShell.Create())
                {
                    // use the runspace pool.
                    //ps.RunspacePool = RsPool;

                    // specify the script code to run.
                    ps.AddScript(scriptContents);

                    // specify the parameters to pass into the script.
                    ps.AddParameters(scriptParameters);

                    // Subscribe to events from output
                    var output = new PSDataCollection<PSObject>();

                    output.DataAdded += delegate (object sender, DataAddedEventArgs e)
                    {
                        var objectsReceived = sender as PSDataCollection<PSObject>;
                        var currentRecord = objectsReceived[e.Index];

                        outputHandler(new LogRecord(DateTime.Now.ToString(), "Data", currentRecord.ToString()));
                    };

                    // subscribe to events from some of the streams

                    /// Handles data-added events for the error stream.
                    /// Note: Uncaught terminating errors will stop the pipeline completely.
                    /// Non-terminating errors will be written to this stream and execution will continue.
                    ps.Streams.Error.DataAdded += delegate (object sender, DataAddedEventArgs e)
                    {
                        var streamObjectsReceived = sender as PSDataCollection<ErrorRecord>;
                        var currentStreamRecord = streamObjectsReceived[e.Index];

                        outputHandler(new LogRecord(DateTime.Now.ToString(), "Error", currentStreamRecord.Exception.ToString()));
                    };

                    /// Handles data-added events for the warning stream.
                    ps.Streams.Warning.DataAdded += delegate (object sender, DataAddedEventArgs e)
                    {
                        var streamObjectsReceived = sender as PSDataCollection<WarningRecord>;
                        var currentStreamRecord = streamObjectsReceived[e.Index];

                        outputHandler(new LogRecord(DateTime.Now.ToString(), "Warning", currentStreamRecord.Message));
                    };

                    /// Handles data-added events for the information stream.
                    /// Note: Write-Host and Write-Information messages will end up in the information stream.
                    ps.Streams.Information.DataAdded += delegate (object sender, DataAddedEventArgs e)
                    {
                        var streamObjectsReceived = sender as PSDataCollection<InformationRecord>;
                        var currentStreamRecord = streamObjectsReceived[e.Index];

                        outputHandler(new LogRecord(DateTime.Now.ToString(), "Info", currentStreamRecord.MessageData.ToString()));
                    };

                    /// Handles data-added events for the progress stream.
                    ps.Streams.Progress.DataAdded += delegate (object sender, DataAddedEventArgs e)
                    {
                        var streamObjectsReceived = sender as PSDataCollection<ProgressRecord>;
                        var currentStreamRecord = streamObjectsReceived[e.Index];

                        outputHandler(new LogRecord(DateTime.Now.ToString(), "Progress",
                            $"{currentStreamRecord.Activity}... {currentStreamRecord.StatusDescription} {currentStreamRecord.PercentComplete}%"));
                    };

                    /// Handles data-added events for the verbose stream.
                    ps.Streams.Verbose.DataAdded += delegate (object sender, DataAddedEventArgs e)
                    {
                        var streamObjectsReceived = sender as PSDataCollection<VerboseRecord>;
                        var currentStreamRecord = streamObjectsReceived[e.Index];

                        outputHandler(new LogRecord(DateTime.Now.ToString(), "Verbose", currentStreamRecord.Message));
                    };

                    /// Handles data-added events for the debug stream.
                    ps.Streams.Debug.DataAdded += delegate (object sender, DataAddedEventArgs e)
                    {
                        var streamObjectsReceived = sender as PSDataCollection<DebugRecord>;
                        var currentStreamRecord = streamObjectsReceived[e.Index];

                        outputHandler(new LogRecord(DateTime.Now.ToString(), "Debug", currentStreamRecord.Message));
                    };

                    await ps.InvokeAsync<PSObject, PSObject>(null, output).ConfigureAwait(false);

                    //// execute the script and await the result.
                    //var pipelineObjects = await ps.InvokeAsync().ConfigureAwait(false);
                    //foreach (var item in pipelineObjects)
                    //{
                    //    outputHandler(item.BaseObject.ToString() + "\n");
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
                Logger.Log(LogLevel.Error, "Failure loading or executing script", ex);
            }
        }
    }
}
