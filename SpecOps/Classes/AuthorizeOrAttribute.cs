using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Classes
{
    public class AuthorizeOrAttribute : TypeFilterAttribute
    {
        public AuthorizeOrAttribute(string[] policies) : base(typeof(AuthorizeOrFilter))
        {
            Arguments = new object[] { policies };
        }
    }
}
