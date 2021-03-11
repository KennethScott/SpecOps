using System;

namespace SpecOps.Models
{
    public class OutputRecord
    {
        public string TimeStamp { get; set; }
        public string Type { get; set; }
        public string Data { get; set; }

        public OutputRecord(string Type, string Data)
        {
            this.TimeStamp = DateTime.Now.ToString();
            this.Type = Type;
            this.Data = Data;
        }

        public OutputRecord(string TimeStamp, string Type, string Data)
        {
            this.TimeStamp = TimeStamp;
            this.Type = Type;
            this.Data = Data;
        }
    }
}
