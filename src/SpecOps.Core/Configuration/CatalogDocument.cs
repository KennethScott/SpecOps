using System.Text.Json;
using System.Text.Json.Serialization;
using SpecOps.Core.Scripts;
using SpecOps.Core.Security;

namespace SpecOps.Core.Configuration;

/// <summary>
/// Everything admins manage in the app: categories, policies, runspace profiles and scripts. Stored in the database
/// as versioned JSON snapshots; the same shape as the "SpecOps" section of specops.json, so files import and export.
/// </summary>
public sealed class CatalogDocument
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public IList<CategoryDefinition> Categories { get; init; } = [];

    public IList<PolicyDefinition> Policies { get; init; } = [];

    public IList<RunspaceProfile> RunspaceProfiles { get; init; } = [];

    public IList<ScriptDefinition> Scripts { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Categories.Count + Policies.Count + RunspaceProfiles.Count + Scripts.Count == 0;

    public static CatalogDocument From(SpecOpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CatalogDocument
        {
            Categories = [.. options.Categories],
            Policies = [.. options.Policies],
            RunspaceProfiles = [.. options.RunspaceProfiles],
            Scripts = [.. options.Scripts],
        }.Clone();
    }

    /// <summary>Copies this catalog into <paramref name="options"/> (as a deep copy, so later edits don't leak).</summary>
    public void ApplyTo(SpecOpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var copy = Clone();
        Replace(options.Categories, copy.Categories);
        Replace(options.Policies, copy.Policies);
        Replace(options.RunspaceProfiles, copy.RunspaceProfiles);
        Replace(options.Scripts, copy.Scripts);
    }

    /// <summary>
    /// The configuration this catalog would produce together with the current runner settings, for validation
    /// before it's saved.
    /// </summary>
    public SpecOpsOptions ToOptions(SpecOpsOptions current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var options = new SpecOpsOptions { ScriptsRoot = current.ScriptsRoot, Runner = current.Runner };
        ApplyTo(options);
        return options;
    }

    public CatalogDocument Clone() => Parse(ToJson());

    /// <summary>Indented JSON, wrapped as <c>{ "SpecOps": { ... } }</c> so it can be pasted into specops.json.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Wrapper { SpecOps = this }, Json);

    /// <summary>
    /// Reads JSON written by <see cref="ToJson"/>, a whole specops.json (comments allowed; other settings ignored) or
    /// a bare <c>{ "Scripts": [...] }</c> object.
    /// </summary>
    /// <exception cref="JsonException">The text isn't valid JSON of that shape.</exception>
    public static CatalogDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }
        );
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a JSON object.");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, SpecOpsOptions.SectionName, StringComparison.OrdinalIgnoreCase))
            {
                root = property.Value;
                break;
            }
        }

        return root.Deserialize<CatalogDocument>(Json) ?? new CatalogDocument();
    }

    private static void Replace<T>(IList<T> target, IList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private sealed class Wrapper
    {
        public CatalogDocument SpecOps { get; init; } = new();
    }
}
