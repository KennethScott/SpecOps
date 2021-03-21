using System;

namespace SpecOps.Models
{
    public class OutputRecord
    {
        public string TimeStamp { get; set; }
        public string Type { get; set; }
        public string Data { get; set; }

        public OutputRecord(OutputLevelName Type, string Data) : this(DateTime.Now.ToString(), Type, Data)
        {
        }

        public OutputRecord(string TimeStamp, OutputLevelName Type, string Data)
        {
            this.TimeStamp = TimeStamp;
            this.Type = Enum.GetName(Type);
            this.Data = Data;
        }
    }
}
