using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Classes
{
    public class MultiplePolicyAuthorizeFilter : IAsyncAuthorizationFilter
    {
        private readonly IAuthorizationService _authorization;
        public string Policys { get; private set; }
        public bool IsAnd { get; private set; }

        public MultiplePolicyAuthorizeFilter(string policys, bool isAnd, IAuthorizationService authorization)
        {
            Policys = policys;
            IsAnd = isAnd;
            _authorization = authorization;
        }

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var policys = Policys.Split(",").ToList();
            if (IsAnd)
            {
                foreach (var policy in policys)
                {
                    var authorized = await _authorization.AuthorizeAsync(context.HttpContext.User, policy);
                    if (!authorized.Succeeded)
                    {
                        context.Result = new ForbidResult();
                        return;
                    }

                }
            }
            else
            {
                foreach (var policy in policys)
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
}
