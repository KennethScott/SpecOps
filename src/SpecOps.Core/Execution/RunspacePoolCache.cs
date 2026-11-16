using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading.Channels;
using Microsoft.PowerShell;
using SpecOps.Core.Scripts;

namespace SpecOps.Core.Execution;

/// <summary>
/// One warm pool per runspace profile, created on first use (module imports are slow, so runspaces are reused).
/// When a profile's settings change, its old pool is retired: idle runspaces close now, in-use ones when their run ends.
/// </summary>
internal sealed class RunspacePoolCache : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Settings, WarmRunspacePool Pool)> _pools = new(
        StringComparer.OrdinalIgnoreCase
    );

    public WarmRunspacePool Get(RunspaceProfile profile, string? defaultExecutionPolicy)
    {
        var policy = profile.ExecutionPolicy ?? defaultExecutionPolicy;
        var settings = $"{policy}|{profile.MinRunspaces}|{profile.MaxRunspaces}|{string.Join(',', profile.Modules)}";

        lock (_lock)
        {
            if (_pools.TryGetValue(profile.Name, out var existing))
            {
                if (existing.Settings == settings)
                {
                    return existing.Pool;
                }

                existing.Pool.Dispose();
            }

            var pool = new WarmRunspacePool(profile, policy);
            _pools[profile.Name] = (settings, pool);
            return pool;
        }
    }

    internal int PoolCount
    {
        get
        {
            lock (_lock)
            {
                return _pools.Count;
            }
        }
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

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (_, pool) in _pools.Values)
            {
                pool.Dispose();
            }

            _pools.Clear();
        }
    }
}

/// <summary>
/// Reusable runspaces for one profile, reset to their initial state between runs so nothing (variables, functions,
/// aliases, modules, drives, location) leaks from one run, or one user, to the next.
/// </summary>
internal sealed class WarmRunspacePool : IDisposable
{
    private readonly InitialSessionState _state;
    private readonly Channel<PooledRunspace> _idle = Channel.CreateUnbounded<PooledRunspace>();

    // A slot is held while a runspace is in use or being created, so idle + in-use never exceeds Max.
    // (Not disposed: a SemaphoreSlim without a wait handle holds no unmanaged resources, and late releases stay safe.)
    private readonly SemaphoreSlim _slots;
    private volatile bool _disposed;
    private int _live;

    public WarmRunspacePool(RunspaceProfile profile, string? executionPolicy)
    {
        _state = RunspacePoolCache.CreateSessionState(executionPolicy, profile.Modules);
        var max = Math.Max(1, Math.Max(profile.MinRunspaces, profile.MaxRunspaces));
        _slots = new SemaphoreSlim(max, max);

        // Pre-warm in the background so the first runs don't pay for module imports.
        var min = Math.Clamp(profile.MinRunspaces, 0, max);
        _ = Task.Run(() => PrewarmAsync(min));
    }

    private async Task PrewarmAsync(int min)
    {
        while (!_disposed && Volatile.Read(ref _live) < min)
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_disposed && Volatile.Read(ref _live) < min)
                {
                    Park(Open());
                }
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    /// <summary>Waits for a free slot, then returns an idle runspace (or opens a new one).</summary>
    public async Task<PooledRunspace> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_idle.Reader.TryRead(out var idle))
            {
                if (idle.IsUsable)
                {
                    return idle;
                }

                Destroy(idle);
            }

            return Open();
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    /// <summary>Resets the runspace and makes it available again; broken runspaces are discarded.</summary>
    public void Return(PooledRunspace runspace)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        try
        {
            if (!_disposed && runspace.TryReset())
            {
                Park(runspace);
            }
            else
            {
                Destroy(runspace);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _idle.Writer.TryComplete();
        while (_idle.Reader.TryRead(out var idle))
        {
            Destroy(idle);
        }
    }

    private PooledRunspace Open()
    {
        var runspace = PooledRunspace.Open(_state);
        Interlocked.Increment(ref _live);
        return runspace;
    }

    /// <summary>Makes a runspace idle; if the pool was disposed meanwhile, the runspace is closed instead of leaked.</summary>
    private void Park(PooledRunspace runspace)
    {
        if (_disposed || !_idle.Writer.TryWrite(runspace))
        {
            Destroy(runspace);
        }
    }

    private void Destroy(PooledRunspace runspace)
    {
        runspace.Dispose();
        Interlocked.Decrement(ref _live);
    }
}

internal sealed class PooledRunspace : IDisposable
{
    /// <summary>Snapshot of what exists right after the runspace opens (including the profile's modules).</summary>
    private const string CaptureBaselineScript = """
        [pscustomobject]@{
            Functions = @(Get-ChildItem function: | ForEach-Object Name)
            Aliases   = @(Get-ChildItem alias: | ForEach-Object Name)
            Modules   = @(Get-Module | ForEach-Object Name)
            Drives    = @(Get-PSDrive | ForEach-Object Name)
            Location  = (Get-Location).Path
        }
        """;

    /// <summary>Removes everything a run added on top of the baseline. Core modules stay loaded (they auto-load anyway).</summary>
    private const string RestoreBaselineScript = """
        param($Baseline)
        Get-ChildItem function: | Where-Object { $_.Name -notin $Baseline.Functions } | Remove-Item -Force -ErrorAction SilentlyContinue
        Get-ChildItem alias: | Where-Object { $_.Name -notin $Baseline.Aliases } | Remove-Item -Force -ErrorAction SilentlyContinue
        Get-Module | Where-Object { $_.Name -notin $Baseline.Modules -and $_.Name -notlike 'Microsoft.PowerShell.*' } |
            Remove-Module -Force -ErrorAction SilentlyContinue
        Get-PSDrive | Where-Object { $_.Name -notin $Baseline.Drives } | Remove-PSDrive -Force -ErrorAction SilentlyContinue
        Set-Location -LiteralPath $Baseline.Location
        """;

    private readonly PSObject _baseline;

    private PooledRunspace(Runspace runspace, PSObject baseline)
    {
        Runspace = runspace;
        _baseline = baseline;
    }

    public Runspace Runspace { get; }

    public bool IsUsable => Runspace.RunspaceStateInfo.State == RunspaceState.Opened;

    public static PooledRunspace Open(InitialSessionState state)
    {
        var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.ThreadOptions = PSThreadOptions.ReuseThread;
        runspace.Open();

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var baseline = ps.AddScript(CaptureBaselineScript).Invoke().Single();
        return new PooledRunspace(runspace, baseline);
    }

    public bool TryReset()
    {
        if (!IsUsable)
        {
            return false;
        }

        try
        {
            using (var ps = PowerShell.Create())
            {
                ps.Runspace = Runspace;
                ps.AddScript(RestoreBaselineScript).AddParameter("Baseline", _baseline).Invoke();
            }

            // Restores the variable table (globals, preference variables, $Error) to its initial state.
            Runspace.ResetRunspaceState();
            return IsUsable;
        }
        catch (Exception ex)
            when (ex is RuntimeException or InvalidRunspaceStateException or PSInvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose() => Runspace.Dispose();
}
