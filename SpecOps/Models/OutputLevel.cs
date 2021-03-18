using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Models
{
    // The purpose of this static class is to act as a simple Enum
    public static class OutputLevel
    {
        public const string Debug = "Debug";
        public const string Error = "Error";
        public const string Info = "Info";
        public const string System = "System";
        public const string Progress = "Progress";
        public const string Data = "Data";
        public const string Verbose = "Verbose";
        public const string Warning = "Warning";
        public const string Unknown = "Unknown";
    }

    // The purpose of this class is to act as a strongly-typed appsettings configuration object
    public class OutputLevelStyles
    {
        public string Debug { get; set; }
        public string Error { get; set; }
        public string Info { get; set; }
        public string System { get; set; }
        public string Progress { get; set; }
        public string Data { get; set; }
        public string Verbose { get; set; }
        public string Warning { get; set; }
        public string Unknown { get; set; }

    }
}
