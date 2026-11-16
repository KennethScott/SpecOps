using System.Globalization;
using SpecOps.Core.Scripts;

namespace SpecOps.Core.Execution;

public sealed record ScriptRunRequest
{
    public const string MaskedValue = "••••••";

    public required string ScriptId { get; init; }

    public required string ScriptName { get; init; }

    /// <summary>Full path of the script file. Either this or <see cref="Code"/> must be set.</summary>
    public string? ScriptPath { get; init; }

    /// <summary>Inline code (Terminal).</summary>
    public string? Code { get; init; }

    /// <summary>Validated, typed parameter values.</summary>
    public IReadOnlyDictionary<string, object> Parameters { get; init; } =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of parameters whose values must never be shown or logged.</summary>
    public IReadOnlySet<string> SensitiveParameters { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pooled runspace configuration; null runs in a fresh, isolated runspace.</summary>
    public RunspaceProfile? RunspaceProfile { get; init; }

    public TimeSpan? Timeout { get; init; }

    public required string UserName { get; init; }

    public string? UserId { get; init; }

    public string? ClientIp { get; init; }

    public static ScriptRunRequest ForScript(
        ScriptDefinition script,
        string scriptPath,
        IReadOnlyDictionary<string, object> parameters,
        RunspaceProfile? profile
    )
    {
        ArgumentNullException.ThrowIfNull(script);
        return new ScriptRunRequest
        {
            ScriptId = script.Id,
            ScriptName = script.Name,
            ScriptPath = scriptPath,
            Parameters = parameters,
            SensitiveParameters = script
                .Parameters.Where(p => p.IsSensitive)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            RunspaceProfile = profile,
            Timeout = script.TimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            UserName = "",
        };
    }

    internal IReadOnlyDictionary<string, string> DisplayParameters() =>
        Parameters.ToDictionary(
            p => p.Key,
            p =>
                SensitiveParameters.Contains(p.Key)
                    ? MaskedValue
                    : Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "",
            StringComparer.OrdinalIgnoreCase
        );
}
