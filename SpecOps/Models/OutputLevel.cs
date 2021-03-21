using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Models
{
    public class OutputLevel
    {
        public string Name { get; set; }
        public string CssClass { get; set; }
    }

    public enum OutputLevelName
    {
        Data,
        Debug,
        Error,
        Info,
        Progress,
        System,
        Unknown,
        Verbose,
        Warning
    }
}
