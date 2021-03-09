using Microsoft.AspNetCore.SignalR;

namespace SpecOps.Classes
{
    public class NameUserIdProvider : IUserIdProvider
    {
        public string GetUserId(HubConnectionContext connection)
        {
            return connection.User?.Identity?.Name;
        }
    }
}
