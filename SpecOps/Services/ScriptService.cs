﻿using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using SpecOps.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using System.Threading.Tasks;

namespace SpecOps.Services
{
    public class ScriptService : IScriptService
    {
        private readonly ScriptSettings scriptSettings;
        private readonly IAppSettingsService appSettingsService;

        public ScriptService(IOptionsSnapshot<ScriptSettings> scriptSettings, IAppSettingsService appSettingsService)
        {
            this.scriptSettings = scriptSettings.Value;
            this.appSettingsService = appSettingsService;
        }

        /// <summary>
        /// Get list of Categories that the user has access to
        /// </summary>
        /// <returns>List of CategoryIds</returns>
        public IEnumerable<string> GetCategories()
        {
            return appSettingsService.AuthorizedCategories().OrderBy(s => s);
        }

        /// <summary>
        /// Get all Scripts
        /// </summary>
        /// <returns>List of Scripts</returns>
        public IEnumerable<Script> GetScripts()
        {
            return scriptSettings.Scripts;
        }

        /// <summary>
        /// Get all scripts in a particular Category
        /// </summary>
        /// <param name="category"></param>
        /// <returns>List of Scripts in the desired Category</returns>
        public IEnumerable<Script> GetScripts(string category)
        {
            return GetScripts()
                    .Where(s => s.CategoryId == category)
                    .Select(s => s)
                    .OrderBy(s => s.Name);
        }

        /// <summary>
        /// Get Script by Id
        /// </summary>
        /// <param name="Id">Unique Id from </param>
        /// <returns></returns>
        public Script GetScript(Guid Id)
        {
            return GetScripts().FirstOrDefault(s => s.Id == Id);
        }

    }
}
