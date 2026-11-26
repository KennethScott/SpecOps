using System.Data.Common;
using SpecOps.Web.Services;

namespace SpecOps.Web.Infrastructure;

/// <summary>Deletes run history past Runner.HistoryRetention: shortly after startup, then every few hours.</summary>
public sealed partial class RunHistoryCleanup(RunHistory history, TimeProvider time, ILogger<RunHistoryCleanup> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    var deleted = await history.PruneAsync(stoppingToken);
                    if (deleted > 0)
                    {
                        LogPruned(deleted);
                    }
                }
                catch (Exception ex) when (ex is DbException or InvalidOperationException)
                {
                    LogPruneFailed(ex);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down, including when startup failed and the host is disposed without a normal stop.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run history: deleted {Count} runs past retention")]
    private partial void LogPruned(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run history cleanup failed")]
    private partial void LogPruneFailed(Exception ex);
}
