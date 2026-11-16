using System.Security.Claims;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;
using SpecOps.Core.Validation;
using SpecOps.Web.Auth;

namespace SpecOps.Web.Services;

public sealed record CategoryScripts(CategoryDefinition Category, IReadOnlyList<ScriptDefinition> Scripts);

public sealed record StartRunResult(ScriptRun? Run, string? Error, IReadOnlyDictionary<string, string> FieldErrors)
{
    public static StartRunResult Fail(string error) => new(null, error, new Dictionary<string, string>());
}

/// <summary>
/// The single place the UI asks "what can this user see/run" and starts runs.
/// Every check happens here, server-side, no matter what the browser sends.
/// </summary>
public sealed class ScriptService(IScriptCatalog catalog, ISpecOpsAuthorizer authorizer, IScriptRunner runner)
{
    public const string TerminalScriptId = "terminal";

    public UserAccess GetAccess(ClaimsPrincipal user) => authorizer.GetAccess(user);

    public IReadOnlyList<CategoryScripts> GetVisibleScripts(ClaimsPrincipal user)
    {
        var access = authorizer.GetAccess(user);
        var scripts = catalog.Scripts;
        return
        [
            .. catalog
                .Categories.Where(c => access.CanAccessCategory(c.Id))
                .Select(c => new CategoryScripts(
                    c,
                    [
                        .. scripts
                            .Where(s => string.Equals(s.CategoryId, c.Id, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase),
                    ]
                ))
                .Where(c => c.Scripts.Count > 0),
        ];
    }

    public ScriptDefinition? GetScript(ClaimsPrincipal user, string scriptId) =>
        catalog.FindScript(scriptId) is { } script && authorizer.GetAccess(user).CanRun(script) ? script : null;

    public CategoryDefinition? GetCategory(string categoryId) =>
        catalog.Categories.FirstOrDefault(c => string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<RunspaceProfile> RunspaceProfiles => catalog.RunspaceProfiles;

    public StartRunResult StartScript(
        ClaimsPrincipal user,
        string scriptId,
        IReadOnlyDictionary<string, string?> input,
        string? clientIp
    )
    {
        ArgumentNullException.ThrowIfNull(user);
        var script = GetScript(user, scriptId);
        if (script is null)
        {
            return StartRunResult.Fail("Script not found, or you don't have access to it.");
        }

        if (!script.IsSupportedOnHost)
        {
            return StartRunResult.Fail($"This script requires {script.Platforms} and can't run on this server.");
        }

        if (script.EnableImpersonation)
        {
            return StartRunResult.Fail(
                "This script runs as the signed-in Windows user, which needs Windows sign-in (coming in a later release)."
            );
        }

        var validation = ParameterValidator.Validate(script.Parameters, input);
        if (!validation.IsValid)
        {
            return new StartRunResult(null, "Some parameters need attention.", validation.Errors);
        }

        var request = ScriptRunRequest.ForScript(
            script,
            catalog.ResolveScriptPath(script),
            validation.Values,
            catalog.FindRunspaceProfile(script.RunspaceProfile)
        );
        var run = runner.Start(WithUser(request, user, clientIp));
        return new StartRunResult(run, null, new Dictionary<string, string>());
    }

    public StartRunResult StartCode(ClaimsPrincipal user, string code, string? runspaceProfile, string? clientIp)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!authorizer.GetAccess(user).IsAdmin)
        {
            return StartRunResult.Fail("Only administrators can use the Terminal.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return StartRunResult.Fail("Enter some PowerShell to run.");
        }

        var run = runner.Start(
            new ScriptRunRequest
            {
                ScriptId = TerminalScriptId,
                ScriptName = "Terminal",
                Code = code,
                RunspaceProfile = catalog.FindRunspaceProfile(runspaceProfile),
                UserName = user.DisplayName(),
                UserId = user.UserId(),
                ClientIp = clientIp,
            }
        );
        return new StartRunResult(run, null, new Dictionary<string, string>());
    }

    /// <summary>Owners see their own runs; admins see everyone's.</summary>
    public bool CanView(ClaimsPrincipal user, ScriptRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return (run.UserId is not null && run.UserId == user.UserId()) || authorizer.GetAccess(user).IsAdmin;
    }

    public ScriptRun? FindRun(ClaimsPrincipal user, Guid runId) =>
        runner.Find(runId) is { } run && CanView(user, run) ? run : null;

    public IReadOnlyList<ScriptRun> GetVisibleRuns(ClaimsPrincipal user) =>
        [.. runner.Runs.Where(r => CanView(user, r))];

    public bool Cancel(ClaimsPrincipal user, Guid runId) => FindRun(user, runId) is not null && runner.Cancel(runId);

    private static ScriptRunRequest WithUser(ScriptRunRequest request, ClaimsPrincipal user, string? clientIp) =>
        new()
        {
            ScriptId = request.ScriptId,
            ScriptName = request.ScriptName,
            ScriptPath = request.ScriptPath,
            Code = request.Code,
            Parameters = request.Parameters,
            SensitiveParameters = request.SensitiveParameters,
            RunspaceProfile = request.RunspaceProfile,
            Timeout = request.Timeout,
            UserName = user.DisplayName(),
            UserId = user.UserId(),
            ClientIp = clientIp,
        };
}
