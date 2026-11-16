using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;

namespace SpecOps.Core.Scripts;

public interface IScriptCatalog
{
    /// <summary>Configured categories plus any category referenced by a script, ordered for display.</summary>
    IReadOnlyList<CategoryDefinition> Categories { get; }

    IReadOnlyList<ScriptDefinition> Scripts { get; }

    IReadOnlyList<RunspaceProfile> RunspaceProfiles { get; }

    ScriptDefinition? FindScript(string id);

    RunspaceProfile? FindRunspaceProfile(string? name);

    string ResolveScriptPath(ScriptDefinition script);

    /// <summary>Configuration problems worth warning about (missing files, duplicate IDs, ...).</summary>
    IReadOnlyList<string> Validate();
}

/// <summary>Script catalog backed by configuration (specops.json). Reflects file changes without a restart.</summary>
public sealed class ConfigurationScriptCatalog(IOptionsMonitor<SpecOpsOptions> options) : IScriptCatalog
{
    private SpecOpsOptions Current => options.CurrentValue;

    public IReadOnlyList<ScriptDefinition> Scripts => [.. Current.Scripts];

    public IReadOnlyList<RunspaceProfile> RunspaceProfiles => [.. Current.RunspaceProfiles];

    // Rebuilt only when the configuration changes (a reload produces a new options instance).
    private volatile CategoryCache? _categoryCache;

    public IReadOnlyList<CategoryDefinition> Categories
    {
        get
        {
            var current = Current;
            if (_categoryCache is { } cache && ReferenceEquals(cache.For, current))
            {
                return cache.Categories;
            }

            var defined = current.Categories.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var id in current.Scripts.Select(s => s.CategoryId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                defined.TryAdd(id, new CategoryDefinition { Id = id });
            }

            IReadOnlyList<CategoryDefinition> categories =
            [
                .. defined
                    .Values.OrderBy(c => c.SortOrder)
                    .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            ];
            _categoryCache = new CategoryCache(current, categories);
            return categories;
        }
    }

    public ScriptDefinition? FindScript(string id) =>
        Current.Scripts.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public RunspaceProfile? FindRunspaceProfile(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Current.RunspaceProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            );

    public string ResolveScriptPath(ScriptDefinition script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(Current.ScriptsRoot, script.Path));
    }

    /// <summary>
    /// Things worth a warning that don't make the configuration invalid (those are rejected by
    /// <see cref="Configuration.SpecOpsOptionsValidator"/>): missing script files, scripts this host can't run,
    /// policies that grant nothing.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var current = Current;
        var categoryIds = Categories.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var script in current.Scripts)
        {
            if (!File.Exists(ResolveScriptPath(script)))
            {
                problems.Add($"Script '{script.Id}': file not found at '{ResolveScriptPath(script)}'.");
            }

            if (!script.IsSupportedOnHost)
            {
                problems.Add(
                    $"Script '{script.Id}': requires {script.Platforms}; it will be shown but can't run on this host."
                );
            }
        }

        foreach (var policy in current.Policies)
        {
            if (policy.Roles.Count == 0 && policy.Groups.Count == 0)
            {
                problems.Add($"Policy '{policy.Name}': no Roles or Groups, so it applies to nobody.");
            }

            foreach (
                var category in policy.Categories.Where(c =>
                    !string.Equals(c, "*", StringComparison.Ordinal) && !categoryIds.Contains(c)
                )
            )
            {
                problems.Add($"Policy '{policy.Name}': category '{category}' has no scripts.");
            }
        }

        return problems;
    }
}

/// <summary>Categories computed for one configuration instance (replaced atomically on reload).</summary>
internal sealed record CategoryCache(SpecOpsOptions For, IReadOnlyList<CategoryDefinition> Categories);
