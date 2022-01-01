using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Models
{
    public class ScriptRunspace
    {
        /// <summary>
        /// String name of the desired enum Microsoft.PowerShell.ExecutionPolicy
        /// https://docs.microsoft.com/en-us/dotnet/api/microsoft.powershell.executionpolicy?view=powershellsdk-7.0.0
        /// </summary>
        public string ExecutionPolicy { get; set; }
        /// <summary>
        /// Minimum number of runspaces for the pool
        /// </summary>
        public int? Min { get; set; }
        /// <summary>
        /// Maximum number of runspaces for the pool
        /// </summary>
        public int? Max { get; set; }
        /// <summary>
        /// Module names to import into the runspace
        /// </summary>
        public IEnumerable<string> Modules { get; set; }
    }
}
