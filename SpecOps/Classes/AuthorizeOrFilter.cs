using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Classes
{
    public class AuthorizeOrFilter : IAsyncAuthorizationFilter
    {
        private readonly IAuthorizationService _authorization;
        public string[] Policies { get; private set; }

        public AuthorizeOrFilter(string[] policies, IAuthorizationService authorization)
        {
            Policies = policies;
            _authorization = authorization;
        }

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            foreach (var policy in Policies)
            {
                var authorized = await _authorization.AuthorizeAsync(context.HttpContext.User, policy);
                if (authorized.Succeeded)
                {
                    return;
                }

            }
            context.Result = new ForbidResult();
            return;
        }
    }
}
