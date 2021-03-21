using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Models
{
    // The purpose of this static class is to act as a simple Enum
    public static class SecurityPolicy
    {
        public const string Admin = "Admin";
        public const string User = "User";
    }

    // The purpose of this class is to act as a strongly-typed appsettings configuration object
    public class SecurityPolicySettings
    {
        public IEnumerable<string> AdminGroups { get; set; }
        public IEnumerable<string> UserGroups { get; set; }
    }
}
