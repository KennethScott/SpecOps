using System.Collections.Generic;

namespace SpecOps.Models
{
    public class Script
    {
        /// <summary>
        /// CategoryIds that start with "Admin" will be restricted to the Admin group
        /// </summary>
        public string CategoryId { get; set; }
        /// <summary>
        /// Unique identifier for the script (generally a GUID)
        /// </summary>
        public string Id { get; set; }
        public string PathAndFilename { get; set; }
        public string Name { get; set; }        
        public string Summary { get; set; }
        public List<ScriptParameter> InputParms { get; set; }

        public string GetContents() 
        { 
            return System.IO.File.ReadAllText(PathAndFilename);
        }
    }
}
