using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;

namespace SpecOps.Tests;

/// <summary>In-memory options monitor; <see cref="Set"/> simulates a configuration reload.</summary>
internal sealed class TestOptions<T>(T value) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public T CurrentValue { get; private set; } = value;

    public T Get(string? name) => CurrentValue;

    public void Set(T value)
    {
        CurrentValue = value;
        foreach (var listener in _listeners.ToList())
        {
            listener(value, Options.DefaultName);
        }
    }

    /// <summary>Mutates the current value in place and notifies listeners.</summary>
    public void Update(Action<T> change)
    {
        change(CurrentValue);
        Set(CurrentValue);
    }

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Unsubscriber(() => _listeners.Remove(listener));
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
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
