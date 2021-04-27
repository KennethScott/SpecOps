using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.Services
{
    public interface IAppSettingsService
    {
        IEnumerable<string> AuthorizedCategories();
    }
}
