using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpecOps.Core.Configuration;

/// <summary>
/// Validates options itself (the inner monitor has no validators) so a broken live edit, e.g. a typo in
/// specops.json, doesn't take the app down: the change is logged and rejected, and the last valid value keeps being
/// served. Before any valid value exists (startup) validation errors are thrown, so misconfiguration fails fast.
/// </summary>
/// <remarks>
/// Validation deliberately isn't registered with the options system: .NET's OptionsMonitor would then validate
/// inside its own change callback, where the exception is swallowed on a file-watcher thread before any listener runs.
/// </remarks>
public sealed partial class LastGoodOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>, IDisposable
    where TOptions : class
{
    private readonly IOptionsMonitor<TOptions> _inner;
    private readonly IValidateOptions<TOptions> _validator;
    private readonly ILogger<LastGoodOptionsMonitor<TOptions>> _logger;
    private readonly IDisposable? _changeSubscription;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TOptions> _lastGood = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TOptions> _reportedInvalid = new(StringComparer.Ordinal);

    public LastGoodOptionsMonitor(
        IOptionsMonitor<TOptions> inner,
        IValidateOptions<TOptions> validator,
        ILogger<LastGoodOptionsMonitor<TOptions>> logger
    )
    {
        _inner = inner;
        _validator = validator;
        _logger = logger;
        // Validate (and log a rejection) as soon as the configuration changes, not on the next read.
        _changeSubscription = inner.OnChange((_, name) => Get(name));
    }

    public TOptions CurrentValue => Get(Options.DefaultName);

    public TOptions Get(string? name)
    {
        name ??= Options.DefaultName;
        var value = _inner.Get(name);
        var result = _validator.Validate(name, value);

        lock (_lock)
        {
            if (result.Succeeded || result.Skipped)
            {
                _lastGood[name] = value;
                _reportedInvalid.Remove(name);
                return value;
            }

            var failures = result.Failures ?? [result.FailureMessage ?? "Invalid configuration."];
            if (!_lastGood.TryGetValue(name, out var previous))
            {
                throw new OptionsValidationException(name, typeof(TOptions), failures);
            }

            // Log each rejected configuration once, not on every read.
            if (!_reportedInvalid.TryGetValue(name, out var reported) || !ReferenceEquals(reported, value))
            {
                _reportedInvalid[name] = value;
                LogRejected(typeof(TOptions).Name, string.Join(Environment.NewLine, failures));
            }

            return previous;
        }
    }

    public IDisposable? OnChange(Action<TOptions, string?> listener) =>
        // Hand listeners the effective value: the new one if valid, otherwise the last good one.
        _inner.OnChange((_, name) => listener(Get(name), name));

    public void Dispose() => _changeSubscription?.Dispose();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Rejected a configuration change to {Options}; keeping the previous configuration. Problems:\n{Problems}"
    )]
    private partial void LogRejected(string options, string problems);
}
