using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Toolbox.Models
{
    public class LogRecord
    {
        public string TimeStamp { get; set; }
        public string Type { get; set; }
        public string Data { get; set; }

        public LogRecord(string TimeStamp, string Type, string Data)
        {
            this.TimeStamp = TimeStamp;
            this.Type = Type;
            this.Data = Data;
        }
    }
}
