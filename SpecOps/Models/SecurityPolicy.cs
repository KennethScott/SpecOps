using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Models
{
    public class SecurityPolicy
    {
        public string Name { get; set; }

        public IEnumerable<string> Groups { get; set; }

        public IEnumerable<string> CategoryIds { get; set; }
    }
}
