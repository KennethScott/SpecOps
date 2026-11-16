using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;
using Microsoft.PowerShell;
using SpecOps.Core.Scripts;

namespace SpecOps.Core.Execution;

/// <summary>
/// One open runspace pool per runspace profile, created on first use and kept warm (module imports are slow).
/// A changed profile gets a new pool; old pools are disposed at shutdown.
/// </summary>
internal sealed class RunspacePoolCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<RunspacePool>> _pools = new(StringComparer.Ordinal);

    public RunspacePool Get(RunspaceProfile profile, string? defaultExecutionPolicy)
    {
        var policy = profile.ExecutionPolicy ?? defaultExecutionPolicy;
        var key = $"{profile.Name}|{policy}|{profile.MinRunspaces}|{profile.MaxRunspaces}|{string.Join(',', profile.Modules)}";
        return _pools.GetOrAdd(key, _ => new Lazy<RunspacePool>(() => Create(profile, policy))).Value;
    }

    internal static InitialSessionState CreateSessionState(string? executionPolicy, IEnumerable<string> modules)
    {
        var state = InitialSessionState.CreateDefault2();
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(executionPolicy))
        {
            state.ExecutionPolicy = Enum.Parse<ExecutionPolicy>(executionPolicy, ignoreCase: true);
        }

        var moduleList = modules.Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
        if (moduleList.Length > 0)
        {
            state.ImportPSModule(moduleList);
        }

        return state;
    }

    private static RunspacePool Create(RunspaceProfile profile, string? executionPolicy)
    {
        var pool = RunspaceFactory.CreateRunspacePool(CreateSessionState(executionPolicy, profile.Modules));
        pool.SetMinRunspaces(Math.Max(1, profile.MinRunspaces));
        pool.SetMaxRunspaces(Math.Max(Math.Max(1, profile.MinRunspaces), profile.MaxRunspaces));
        pool.ThreadOptions = PSThreadOptions.ReuseThread;
        pool.Open();
        return pool;
    }

    public void Dispose()
    {
        foreach (var lazy in _pools.Values.Where(l => l.IsValueCreated))
        {
            lazy.Value.Dispose();
        }

        _pools.Clear();
    }
}
