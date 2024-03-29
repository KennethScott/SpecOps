﻿using Serilog.Core;
using Serilog.Events;
using System;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace SpecOps.Enrichers
{
    public class ClientIpEnricher : ILogEventEnricher
    {
        private const string IpAddressPropertyName = "ClientIp";
        private const string IpAddresstItemKey = "Serilog_ClientIp";

        private readonly IHttpContextAccessor _contextAccessor;

        public ClientIpEnricher()
        {
            _contextAccessor = new HttpContextAccessor();
        }

        internal ClientIpEnricher(IHttpContextAccessor contextAccessor)
        {
            _contextAccessor = contextAccessor;
        }

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var httpContext = _contextAccessor.HttpContext;
            if (httpContext == null)
                return;

            if (httpContext.Items[IpAddresstItemKey] is LogEventProperty logEventProperty)
            {
                logEvent.AddPropertyIfAbsent(logEventProperty);
                return;
            }

            var ipAddress = GetIpAddress();

            if (string.IsNullOrWhiteSpace(ipAddress))
                ipAddress = "unknown";

            var ipAddressProperty = new LogEventProperty(IpAddressPropertyName, new ScalarValue(ipAddress));
            httpContext.Items.Add(IpAddresstItemKey, ipAddressProperty);

            logEvent.AddPropertyIfAbsent(ipAddressProperty);
        }

         private string GetIpAddress()
         {
             var ipAddress = _contextAccessor.HttpContext.Request.Headers["X-forwarded-for"].FirstOrDefault();

             if (!string.IsNullOrEmpty(ipAddress))
             {
                 return GetIpAddressFromProxy(ipAddress);
             }
         
             return _contextAccessor.HttpContext.Connection.RemoteIpAddress.ToString();
         }
        
        private string GetIpAddressFromProxy(string proxiedIpList)
        {
            var addresses = proxiedIpList.Split(',');

            if (addresses.Length != 0)
            {
                // If IP contains port, it will be after the last : (IPv6 uses : as delimiter and could have more of them)
                return addresses[0].Contains(":")
                    ? addresses[0].Substring(0, addresses[0].LastIndexOf(":", StringComparison.Ordinal))
                    : addresses[0];
            }

            return string.Empty;
        }
    
    }
}
