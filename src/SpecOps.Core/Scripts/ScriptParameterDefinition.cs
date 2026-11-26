namespace SpecOps.Core.Scripts;

public enum ParameterType
{
    Text,
    TextArea,
    Number,
    Range,
    Date,
    Time,
    DateTime,
    Email,
    Password,
    Select,
    Checkbox,
}

public sealed class ScriptParameterDefinition
{
    /// <summary>Parameter name exactly as declared in the script's param() block.</summary>
    public string Name { get; set; } = "";

    /// <summary>Friendly label; defaults to <see cref="Name"/>.</summary>
    public string? Label { get; set; }

    public ParameterType Type { get; set; } = ParameterType.Text;

    public string? Description { get; set; }

    public string? Placeholder { get; set; }

    public bool Required { get; set; }

    /// <summary>Initial value shown in the form.</summary>
    public string? Default { get; set; }

    /// <summary>Minimum for Number/Range.</summary>
    public double? Min { get; set; }

    /// <summary>Maximum for Number/Range.</summary>
    public double? Max { get; set; }

    /// <summary>Step for Number/Range.</summary>
    public double? Step { get; set; }

    /// <summary>Regex the whole value must match (text-like types).</summary>
    public string? Pattern { get; set; }

    /// <summary>Choices for Select.</summary>
    public IList<SelectOption> Options { get; init; } = [];

    /// <summary>Suggested values for free-text inputs (shown as an autocomplete list).</summary>
    public IList<string> Suggestions { get; init; } = [];

    /// <summary>Never log or store the value. Always true for Password.</summary>
    public bool Sensitive { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSensitive => Sensitive || Type == ParameterType.Password;
}

public sealed class SelectOption
{
    public string Value { get; set; } = "";

    public string? Label { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Value : Label;
}
