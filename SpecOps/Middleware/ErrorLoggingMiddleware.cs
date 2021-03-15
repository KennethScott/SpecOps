using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Middleware
{
    public class ErrorLoggingMiddleware
    {
        private readonly RequestDelegate Next;
        private readonly ILogger<ErrorLoggingMiddleware> Logger;

        public ErrorLoggingMiddleware(RequestDelegate next, ILogger<ErrorLoggingMiddleware> logger)
        {
            Next = next;
            this.Logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            try
            {
                await Next(context);
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Message}");
                Logger.Log(LogLevel.Error, e, "Unhandled exception");
                throw;
            }
        }
    }
}
