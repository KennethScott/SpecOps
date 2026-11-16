using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;

namespace SpecOps.Tests;

internal sealed class TestOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal static class TestOptions
{
    public static TestOptions<SpecOpsOptions> Create(Action<SpecOpsOptions>? configure = null)
    {
        var options = new SpecOpsOptions();
        configure?.Invoke(options);
        return new TestOptions<SpecOpsOptions>(options);
    }
}
