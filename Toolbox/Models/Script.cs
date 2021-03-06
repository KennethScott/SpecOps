using System.Collections.Generic;

namespace Toolbox.Models
{
    public class Script
    {
        public string CategoryId { get; set; }
        public string Id { get; set; }
        public string PathAndFilename { get; set; }
        public string Name { get; set; }        
        public string Summary { get; set; }
        public string RestrictedToAdGroups { get; set; }
        public List<ScriptParameter> InputParms { get; set; }

    }
}
