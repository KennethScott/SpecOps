using LiteDB;
using Microsoft.Extensions.Options;
using SpecOps.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpecOps.LiteDb
{
    public class LiteDbContext : ILiteDbContext
    {
        public LiteDatabase Database { get; }

        public LiteDbContext(IOptionsSnapshot<AppSettings> appSettings)
        {
            //Database = new LiteDatabase(appSettings.Value.DatabaseLocation);
        }
    }
}
