using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Classes
{
    public class MultiplePolicyAuthorizeAttribute : TypeFilterAttribute
    {
        public MultiplePolicyAuthorizeAttribute(string policys, bool isAnd = false) : base(typeof(MultiplePolicyAuthorizeFilter))
        {
            Arguments = new object[] { policys, isAnd };
        }
    }
}
