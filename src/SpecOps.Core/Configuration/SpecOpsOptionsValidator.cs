using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.PowerShell;
using SpecOps.Core.Hosting;
using SpecOps.Core.Scripts;

namespace SpecOps.Core.Configuration;

/// <summary>
/// Rejects configuration that can't work (duplicate IDs, unknown references, impossible limits, bad regexes...).
/// At startup this fails fast; on a live reload the change is rejected and the last good configuration stays active
/// (see <see cref="LastGoodOptionsMonitor{TOptions}"/>).
/// </summary>
public sealed partial class SpecOpsOptionsValidator : IValidateOptions<SpecOpsOptions>
{
    public ValidateOptionsResult Validate(string? name, SpecOpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        ValidateRunner(options.Runner, errors);
        ValidateCategories(options, errors);
        ValidatePolicies(options, errors);
        ValidateProfiles(options, errors);
        ValidateScripts(options, errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateRunner(RunnerOptions runner, List<string> errors)
    {
        void AtLeast(string setting, int value, int minimum)
        {
            if (value < minimum)
            {
                errors.Add($"Runner.{setting} must be at least {minimum} (was {value}).");
            }
        }

        AtLeast(nameof(runner.MaxConcurrentRuns), runner.MaxConcurrentRuns, 1);
        AtLeast(nameof(runner.MaxActiveRunsPerUser), runner.MaxActiveRunsPerUser, 1);
        AtLeast(nameof(runner.MaxRunsPerMinutePerUser), runner.MaxRunsPerMinutePerUser, 1);
        AtLeast(nameof(runner.MaxRetainedRuns), runner.MaxRetainedRuns, 10);
        AtLeast(nameof(runner.MaxRetainedOutputRecords), runner.MaxRetainedOutputRecords, runner.MaxOutputRecords);
        AtLeast(nameof(runner.MaxOutputRecords), runner.MaxOutputRecords, 100);
        AtLeast(nameof(runner.MaxResultRows), runner.MaxResultRows, 0);

        if (runner.OutputWidth is < 40 or > 2000)
        {
            errors.Add($"Runner.OutputWidth must be between 40 and 2000 (was {runner.OutputWidth}).");
        }

        if (runner.DefaultTimeout <= TimeSpan.Zero)
        {
            errors.Add("Runner.DefaultTimeout must be positive.");
        }

        if (runner.CompletedRunRetention <= TimeSpan.Zero)
        {
            errors.Add("Runner.CompletedRunRetention must be positive.");
        }

        ValidateExecutionPolicy("Runner.ExecutionPolicy", runner.ExecutionPolicy, errors);
    }

    private static void ValidateCategories(SpecOpsOptions options, List<string> errors)
    {
        foreach (var category in options.Categories.Where(c => string.IsNullOrWhiteSpace(c.Id)))
        {
            errors.Add($"Category '{category.Name}': Id is required.");
        }

        AddDuplicates("category id", options.Categories.Select(c => c.Id), errors);
    }

    private static void ValidatePolicies(SpecOpsOptions options, List<string> errors)
    {
        foreach (var policy in options.Policies.Where(p => string.IsNullOrWhiteSpace(p.Name)))
        {
            errors.Add($"A policy granting [{string.Join(", ", policy.Categories)}] has no Name.");
        }

        AddDuplicates("policy name", options.Policies.Select(p => p.Name), errors);
    }

    private static void ValidateProfiles(SpecOpsOptions options, List<string> errors)
    {
        foreach (var profile in options.RunspaceProfiles)
        {
            var label = $"Runspace profile '{profile.Name}'";
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                errors.Add("A runspace profile has no Name.");
            }

            if (profile.MinRunspaces < 0 || profile.MaxRunspaces < 1 || profile.MinRunspaces > profile.MaxRunspaces)
            {
                errors.Add($"{label}: needs 0 <= MinRunspaces <= MaxRunspaces and MaxRunspaces >= 1.");
            }

            ValidateExecutionPolicy($"{label} ExecutionPolicy", profile.ExecutionPolicy, errors);
        }

        AddDuplicates("runspace profile name", options.RunspaceProfiles.Select(p => p.Name), errors);
    }

    private static void ValidateScripts(SpecOpsOptions options, List<string> errors)
    {
        var profiles = options.RunspaceProfiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var script in options.Scripts)
        {
            var label = $"Script '{script.Id}'";
            if (string.IsNullOrWhiteSpace(script.Id) || !ScriptIdRegex().IsMatch(script.Id))
            {
                errors.Add($"{label}: Id is required and may only contain letters, digits, '-', '_' and '.'.");
            }

            foreach (
                var (field, value) in new[]
                {
                    ("Name", script.Name),
                    ("CategoryId", script.CategoryId),
                    ("Path", script.Path),
                }
            )
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    errors.Add($"{label}: {field} is required.");
                }
            }

            if (!string.IsNullOrWhiteSpace(script.RunspaceProfile) && !profiles.Contains(script.RunspaceProfile))
            {
                errors.Add($"{label}: unknown runspace profile '{script.RunspaceProfile}'.");
            }

            if (script.TimeoutMinutes is <= 0)
            {
                errors.Add($"{label}: TimeoutMinutes must be positive.");
            }

            if (script.Platforms == ScriptPlatforms.None)
            {
                errors.Add($"{label}: Platforms can't be None.");
            }

            ValidateParameters(label, script.Parameters, errors);
        }

        AddDuplicates("script id", options.Scripts.Select(s => s.Id), errors);
    }

    private static void ValidateParameters(
        string scriptLabel,
        IEnumerable<ScriptParameterDefinition> parameters,
        List<string> errors
    )
    {
        var list = parameters.ToList();
        foreach (var p in list)
        {
            var label = $"{scriptLabel} parameter '{p.Name}'";
            if (!ParameterNameRegex().IsMatch(p.Name))
            {
                errors.Add($"{label}: Name must be a valid PowerShell parameter name.");
            }
            else if (p.Name.StartsWith("SpecOps", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{label}: names starting with 'SpecOps' are reserved.");
            }

            if (p.Type == ParameterType.Select)
            {
                if (p.Options.Count == 0)
                {
                    errors.Add($"{label}: Select needs at least one option.");
                }
                else if (p.Default is { Length: > 0 } d && !p.Options.Any(o => o.Value == d))
                {
                    errors.Add($"{label}: Default '{d}' isn't one of the options.");
                }
            }

            if (p.Min is { } min && p.Max is { } max && min > max)
            {
                errors.Add($"{label}: Min is greater than Max.");
            }

            if (!string.IsNullOrEmpty(p.Pattern))
            {
                try
                {
                    _ = new Regex(p.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"{label}: Pattern isn't a valid regular expression ({ex.Message}).");
                }
            }
        }

        AddDuplicates($"{scriptLabel} parameter name", list.Select(p => p.Name), errors);
    }

    private static void ValidateExecutionPolicy(string setting, string? value, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Enum.TryParse<ExecutionPolicy>(value, ignoreCase: true, out _))
        {
            errors.Add(
                $"{setting} '{value}' isn't a PowerShell execution policy ({string.Join(", ", Enum.GetNames<ExecutionPolicy>())})."
            );
        }
    }

    private static void AddDuplicates(string what, IEnumerable<string> values, List<string> errors)
    {
        foreach (
            var duplicate in values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
        )
        {
            errors.Add($"Duplicate {what} '{duplicate.Key}'.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ScriptIdRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ParameterNameRegex();
}
