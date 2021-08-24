using Serilog.Core;
using Serilog.Events;
using Microsoft.AspNetCore.Http;

namespace SpecOps.Enrichers
{
    public class ClientUserNameEnricher : ILogEventEnricher
    {
        private const string ClientUserNamePropertyName = "ClientUserName";
        private const string ClientUserNameItemKey = "Serilog_ClientUserName";

        private readonly IHttpContextAccessor _contextAccessor;

        public ClientUserNameEnricher() : this(new HttpContextAccessor())
        {
        }

        internal ClientUserNameEnricher(IHttpContextAccessor contextAccessor)
        {
            _contextAccessor = contextAccessor;
        }

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var httpContext = _contextAccessor.HttpContext;
            if (httpContext == null)
                return;

            if (httpContext.Items[ClientUserNameItemKey] is LogEventProperty logEventProperty)
            {
                logEvent.AddPropertyIfAbsent(logEventProperty);
                return;
            }

            var userName = httpContext.User?.Identity?.Name == null ? "(anonymous)" : httpContext.User.Identity.Name;

            var clientUserNameProperty = new LogEventProperty(ClientUserNamePropertyName, new ScalarValue(userName));
            httpContext.Items.Add(ClientUserNameItemKey, clientUserNameProperty);

            logEvent.AddPropertyIfAbsent(clientUserNameProperty);
        }
    }
}