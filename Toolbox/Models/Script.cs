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
        public string PathAndFilename { get; set; }
        public ScriptType Type { get; set; }
        public string Contents { get; set; }
        public string Description { get; set; }
        public List<(string Name, string Type)> InputParms { get; set; }

    }
}
