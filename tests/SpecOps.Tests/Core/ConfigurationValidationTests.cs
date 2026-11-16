using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;
using SpecOps.Core.Scripts;

namespace SpecOps.Tests.Core;

public class ConfigurationValidationTests
{
    private static readonly SpecOpsOptionsValidator Validator = new();

    private static SpecOpsOptions Valid() =>
        new()
        {
            Scripts =
            {
                new ScriptDefinition
                {
                    Id = "demo",
                    Name = "Demo",
                    CategoryId = "demo",
                    Path = "Demo.ps1",
                },
            },
        };

    private static IEnumerable<string> Errors(SpecOpsOptions options) =>
        Validator.Validate(null, options).Failures ?? [];

    [Fact]
    public void A_minimal_configuration_is_valid()
    {
        Assert.True(Validator.Validate(null, Valid()).Succeeded);
    }

    [Fact]
    public void The_sample_configuration_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "specops.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build();
        var options = new SpecOpsOptions();
        ConfigurationBinder.Bind(configuration.GetSection(SpecOpsOptions.SectionName), options);

        Assert.Empty(Errors(options));
        Assert.NotEmpty(options.Scripts);
    }

    [Fact]
    public void Rejects_duplicates_and_missing_fields()
    {
        var options = Valid();
        options.Scripts.Add(new ScriptDefinition { Id = "DEMO", CategoryId = "x" });

        var errors = Errors(options).ToList();

        Assert.Contains(errors, e => e.Contains("Duplicate script id", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Name is required", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Path is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_unknown_profiles_and_bad_execution_policies()
    {
        var options = Valid();
        options.Scripts[0].RunspaceProfile = "missing";
        options.Runner.ExecutionPolicy = "Whatever";
        options.RunspaceProfiles.Add(
            new RunspaceProfile
            {
                Name = "p",
                MinRunspaces = 3,
                MaxRunspaces = 1,
            }
        );

        var errors = Errors(options).ToList();

        Assert.Contains(errors, e => e.Contains("unknown runspace profile 'missing'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("isn't a PowerShell execution policy", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("MinRunspaces <= MaxRunspaces", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_bad_parameters()
    {
        var options = Valid();
        options.Scripts[0].Parameters.Add(new ScriptParameterDefinition { Name = "bad name" });
        options.Scripts[0].Parameters.Add(new ScriptParameterDefinition { Name = "SpecOpsUser" });
        options
            .Scripts[0]
            .Parameters.Add(
                new ScriptParameterDefinition
                {
                    Name = "Env",
                    Type = ParameterType.Select,
                    Default = "prod",
                    Options = { new SelectOption { Value = "dev" } },
                }
            );
        options.Scripts[0].Parameters.Add(new ScriptParameterDefinition { Name = "Code", Pattern = "([" });
        options
            .Scripts[0]
            .Parameters.Add(
                new ScriptParameterDefinition
                {
                    Name = "N",
                    Min = 10,
                    Max = 1,
                }
            );

        var errors = Errors(options).ToList();

        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void Rejects_impossible_runner_limits()
    {
        var options = Valid();
        options.Runner.MaxConcurrentRuns = 0;
        options.Runner.DefaultTimeout = TimeSpan.Zero;

        Assert.Equal(2, Errors(options).Count());
    }

    [Fact]
    public void Last_good_monitor_keeps_serving_the_previous_value_after_an_invalid_change()
    {
        var inner = new ValidatingMonitor(Valid());
        var monitor = new LastGoodOptionsMonitor<SpecOpsOptions>(
            inner,
            Validator,
            NullLogger<LastGoodOptionsMonitor<SpecOpsOptions>>.Instance
        );
        var good = monitor.CurrentValue;

        var broken = Valid();
        broken.Scripts[0].Name = "";
        inner.Value = broken;

        Assert.Same(good, monitor.CurrentValue);

        var fixedValue = Valid();
        fixedValue.Scripts[0].Name = "Fixed";
        inner.Value = fixedValue;
        Assert.Equal("Fixed", monitor.CurrentValue.Scripts[0].Name);
    }

    [Fact]
    public void Last_good_monitor_fails_fast_when_there_is_no_good_value_yet()
    {
        var broken = Valid();
        broken.Scripts[0].Name = "";
        var monitor = new LastGoodOptionsMonitor<SpecOpsOptions>(
            new ValidatingMonitor(broken),
            Validator,
            NullLogger<LastGoodOptionsMonitor<SpecOpsOptions>>.Instance
        );

        Assert.Throws<OptionsValidationException>(() => monitor.CurrentValue);
    }

    [Fact]
    public void Invalid_live_reload_through_the_real_options_system_keeps_the_last_good_value()
    {
        var source = new Dictionary<string, string?>
        {
            ["SpecOps:Scripts:0:Id"] = "demo",
            ["SpecOps:Scripts:0:Name"] = "Demo",
            ["SpecOps:Scripts:0:CategoryId"] = "demo",
            ["SpecOps:Scripts:0:Path"] = "Demo.ps1",
            ["SpecOps:Runner:MaxConcurrentRuns"] = "3",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(source).Build();
        using var services = new ServiceCollection()
            .AddOptions<SpecOpsOptions>()
            .Bind(configuration.GetSection(SpecOpsOptions.SectionName))
            .Services.BuildServiceProvider();
        using var monitor = new LastGoodOptionsMonitor<SpecOpsOptions>(
            services.GetRequiredService<IOptionsMonitor<SpecOpsOptions>>(),
            Validator,
            NullLogger<LastGoodOptionsMonitor<SpecOpsOptions>>.Instance
        );
        var notified = new List<int>();
        using var subscription = monitor.OnChange((value, _) => notified.Add(value.Runner.MaxConcurrentRuns));
        Assert.Equal(3, monitor.CurrentValue.Runner.MaxConcurrentRuns);

        configuration["SpecOps:Runner:MaxConcurrentRuns"] = "0";
        configuration.Reload();

        Assert.Equal(3, monitor.CurrentValue.Runner.MaxConcurrentRuns);
        Assert.Equal([3], notified);

        configuration["SpecOps:Runner:MaxConcurrentRuns"] = "5";
        configuration.Reload();

        Assert.Equal(5, monitor.CurrentValue.Runner.MaxConcurrentRuns);
    }

    /// <summary>A plain monitor (no validation of its own), like the inner monitor in the app.</summary>
    private sealed class ValidatingMonitor(SpecOpsOptions value) : IOptionsMonitor<SpecOpsOptions>
    {
        public SpecOpsOptions Value { get; set; } = value;

        public SpecOpsOptions CurrentValue => Value;

        public SpecOpsOptions Get(string? name) => Value;

        public IDisposable? OnChange(Action<SpecOpsOptions, string?> listener) => null;
    }
}
