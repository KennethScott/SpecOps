﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Models
{
    public class AppSettings
    {
        public IEnumerable<SecurityPolicy> SecurityPolicies { get; set; }

        public IEnumerable<OutputLevel> OutputLevels { get; set; }

        public string DatabaseLocation { get; set; }
    }
}
