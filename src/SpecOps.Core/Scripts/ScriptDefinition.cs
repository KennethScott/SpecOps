using SpecOps.Core.Hosting;

namespace SpecOps.Core.Scripts;

public sealed class ScriptDefinition
{
    /// <summary>Unique, URL-friendly identifier (e.g. "reset-password").</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string CategoryId { get; set; } = "";

    public string? Summary { get; set; }

    /// <summary>Path to the .ps1 file, absolute or relative to <c>ScriptsRoot</c>.</summary>
    public string Path { get; set; } = "";

    public IList<ScriptParameterDefinition> Parameters { get; init; } = [];

    /// <summary>Name of a <see cref="RunspaceProfile"/>; when empty the script runs in a fresh, isolated runspace.</summary>
    public string? RunspaceProfile { get; set; }

    /// <summary>Operating systems the script can run on (e.g. "Windows" for scripts using the ActiveDirectory module).</summary>
    public ScriptPlatforms Platforms { get; set; } = ScriptPlatforms.Any;

    /// <summary>Overrides the runner's default timeout.</summary>
    public int? TimeoutMinutes { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSupportedOnHost => HostPlatform.Supports(Platforms);
}

public sealed class CategoryDefinition
{
    public string Id { get; set; } = "";

    public string? Name { get; set; }

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public sealed class RunspaceProfile
{
    public string Name { get; set; } = "";

    /// <summary>Name of a Microsoft.PowerShell.ExecutionPolicy value (Windows only).</summary>
    public string? ExecutionPolicy { get; set; }

    /// <summary>Modules imported into every runspace in the pool.</summary>
    public IList<string> Modules { get; init; } = [];

    public int MinRunspaces { get; set; } = 1;

    public int MaxRunspaces { get; set; } = 4;
}
