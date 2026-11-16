using SpecOps.Core.Execution;

namespace SpecOps.Web.Infrastructure;

/// <summary>
/// Ties the script runner to the host's lifetime: on shutdown (Ctrl+C, container stop, IIS recycle) new runs are
/// refused and active ones are cancelled and awaited within the host's shutdown timeout.
/// </summary>
public sealed class ScriptRunnerLifetime(IScriptRunner runner) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => runner.StopAsync(cancellationToken);
}
