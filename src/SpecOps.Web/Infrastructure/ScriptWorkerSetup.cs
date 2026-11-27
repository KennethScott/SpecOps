using Microsoft.Extensions.Logging.Console;
using SpecOps.Core.Execution;

namespace SpecOps.Web.Infrastructure;

/// <summary>
/// Scripts run in a separate worker process, so one that crashes, hangs or leaks memory can't take the web app down.
/// The worker is this same app started with <see cref="Argument"/>, under the same account, with the same environment.
/// </summary>
internal static class ScriptWorkerSetup
{
    public const string Argument = "--script-worker";

    /// <summary>This app's own executable (apphost), or <c>dotnet SpecOps.Web.dll</c> where there is none (Docker).</summary>
    public static ScriptWorkerCommand Command()
    {
        var dll = typeof(ScriptWorkerSetup).Assembly.Location;
        var appHost = Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null);
        if (File.Exists(appHost))
        {
            return new ScriptWorkerCommand(appHost, [Argument]);
        }

        var dotnet =
            Environment.ProcessPath is { } path
            && string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? path
                : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new ScriptWorkerCommand(dotnet, [dll, Argument]);
    }

    /// <summary>The worker's whole life. Its log goes to stderr, which the web app writes to its own log.</summary>
    public static async Task RunAsync(string pipeName)
    {
        using (
            var loggerFactory = LoggerFactory.Create(logging =>
                logging
                    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
                    .AddSimpleConsole(o =>
                    {
                        o.SingleLine = true;
                        o.ColorBehavior = LoggerColorBehavior.Disabled;
                    })
            )
        )
        {
            await ScriptWorker.RunAsync(pipeName, loggerFactory);
        }

        // The web app has gone. Exit now, even if scripts are still running: there's nobody to report to.
        Environment.Exit(0);
    }
}
