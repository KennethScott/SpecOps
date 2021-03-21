using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Models
{
    public class AppSettings
    {
        public SecurityPolicySettings SecurityPolicies { get; set; }

        public IEnumerable<OutputLevel> OutputLevels { get; set; }

    }
}
