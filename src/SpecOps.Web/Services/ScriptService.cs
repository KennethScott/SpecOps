using System.Security.Claims;
using System.Text.Json;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;
using SpecOps.Core.Validation;
using SpecOps.Web.Auth;

namespace SpecOps.Web.Services;

public sealed record CategoryScripts(CategoryDefinition Category, IReadOnlyList<ScriptDefinition> Scripts);

public sealed record StartRunResult(ScriptRun? Run, string? Error, IReadOnlyDictionary<string, string> FieldErrors)
{
    public static StartRunResult Fail(string error) =>
        new(null, error, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// The single place the UI asks "what can this user see/run" and starts runs.
/// Every check happens here, server-side, no matter what the browser sends, and every run is audited.
/// </summary>
public sealed class ScriptService(
    IScriptCatalog catalog,
    ISpecOpsAuthorizer authorizer,
    IScriptRunner runner,
    AuditLog audit,
    RunHistory history,
    CredentialVault vault
)
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

    public async Task<StartRunResult> StartScriptAsync(
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
            if (catalog.FindScript(scriptId) is not null)
            {
                // The script exists but the user isn't allowed to run it: worth knowing about.
                await audit.RecordAsync(user, AuditActions.ScriptDenied, scriptId, clientIp: clientIp);
            }

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

        // Credential parameters: the validator checked the id is one the script allows; swap it for the real thing.
        var values = new Dictionary<string, object>(validation.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in script.Parameters.Where(p => p.Type == ParameterType.Credential))
        {
            if (values.TryGetValue(parameter.Name, out var value) && value is string credentialId)
            {
                if (await vault.GetCredentialAsync(credentialId) is not { } credential)
                {
                    return StartRunResult.Fail(
                        $"The credential '{credentialId}' for {parameter.DisplayLabel} doesn't exist any more; ask an administrator."
                    );
                }

                values[parameter.Name] = credential;
            }
        }

        var request = ScriptRunRequest.ForScript(
            script,
            catalog.ResolveScriptPath(script),
            values,
            catalog.FindRunspaceProfile(script.RunspaceProfile)
        ) with
        {
            UserName = user.DisplayName(),
            UserId = user.UserId(),
            ClientIp = clientIp,
        };

        return await StartAsync(user, request, AuditActions.ScriptRun, run => JsonSerializer.Serialize(run.Parameters));
    }

    public async Task<StartRunResult> StartCodeAsync(
        ClaimsPrincipal user,
        string code,
        string? runspaceProfile,
        string? clientIp
    )
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!authorizer.GetAccess(user).IsAdmin)
        {
            await audit.RecordAsync(user, AuditActions.ScriptDenied, TerminalScriptId, code, clientIp);
            return StartRunResult.Fail("Only administrators can use the Terminal.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return StartRunResult.Fail("Enter some PowerShell to run.");
        }

        var request = new ScriptRunRequest
        {
            ScriptId = TerminalScriptId,
            ScriptName = "Terminal",
            Code = code,
            RunspaceProfile = catalog.FindRunspaceProfile(runspaceProfile),
            UserName = user.DisplayName(),
            UserId = user.UserId(),
            ClientIp = clientIp,
        };

        return await StartAsync(user, request, AuditActions.TerminalRun, _ => code);
    }

    /// <summary>Owners see their own runs; admins see everyone's.</summary>
    public bool CanView(ClaimsPrincipal user, ScriptRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return (run.UserId is not null && string.Equals(run.UserId, user.UserId(), StringComparison.Ordinal))
            || authorizer.GetAccess(user).IsAdmin;
    }

    /// <summary>A live run from the runner, or a finished one from run history; null if missing or not visible.</summary>
    public async Task<ScriptRun?> FindRunAsync(ClaimsPrincipal user, Guid runId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var run = runner.Find(runId);
        if (run is null && await history.FindAsync(runId) is { } stored)
        {
            run = ScriptRun.FromSnapshot(stored);
        }

        return run is not null && CanView(user, run) ? run : null;
    }

    /// <summary>The user's most recent runs (everyone's for admins), newest first, with live status for active ones.</summary>
    public async Task<IReadOnlyList<RunSummary>> GetRunsAsync(ClaimsPrincipal user, int take)
    {
        ArgumentNullException.ThrowIfNull(user);
        var isAdmin = authorizer.GetAccess(user).IsAdmin;
        if (!isAdmin && user.UserId() is null)
        {
            return [];
        }

        var stored = await history.ListAsync(isAdmin ? null : user.UserId(), take);
        // The runner has the up-to-date status of active runs (and any not saved yet).
        var live = runner.Runs.Where(r => CanView(user, r)).ToDictionary(r => r.Id);
        return
        [
            .. stored
                .Select(s => live.Remove(s.Id, out var run) ? RunSummary.From(run) : s)
                .Concat(live.Values.Select(RunSummary.From))
                .OrderByDescending(r => r.QueuedAt)
                .Take(take),
        ];
    }

    /// <summary>True while any run this user can see is queued or running (cheap: the runner's memory only).</summary>
    public bool HasActiveRuns(ClaimsPrincipal user) => runner.Runs.Any(r => !r.IsFinished && CanView(user, r));

    public async Task<bool> CancelAsync(ClaimsPrincipal user, Guid runId)
    {
        if (runner.Find(runId) is not { } run || !CanView(user, run) || !runner.Cancel(runId))
        {
            return false;
        }

        await audit.RecordAsync(user, AuditActions.RunCancelled, $"{run.ScriptId} {run.Id}");
        return true;
    }

    private async Task<StartRunResult> StartAsync(
        ClaimsPrincipal user,
        ScriptRunRequest request,
        string auditAction,
        Func<ScriptRun, string> auditDetails
    )
    {
        ScriptRun run;
        try
        {
            run = runner.Start(request);
        }
        catch (RunRejectedException ex)
        {
            return StartRunResult.Fail(ex.Message);
        }

        await audit.RecordAsync(user, auditAction, $"{request.ScriptId} {run.Id}", auditDetails(run), request.ClientIp);
        return new StartRunResult(run, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
