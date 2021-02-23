using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Toolbox.Models
{
    public enum ScriptType
    {
        PowerShell,
        CSharp
    }

    public class Script
    {
        public ScriptType Type { get; set; }
        public string Contents { get; set; }
    }
}
