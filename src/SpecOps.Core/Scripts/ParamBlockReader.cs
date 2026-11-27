using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text;

namespace SpecOps.Core.Scripts;

/// <summary>Parameters found in a script's param() block, plus anything that couldn't be understood.</summary>
public sealed record ParamBlockResult(
    IReadOnlyList<ScriptParameterDefinition> Parameters,
    IReadOnlyList<string> Problems
);

/// <summary>
/// Reads a script's param() block with PowerShell's own parser (nothing is run) and turns each parameter into a form
/// field: its type, Mandatory, default value, ValidateSet choices, ValidateRange/ValidateLength limits,
/// ValidatePattern, HelpMessage and comment-based help (.PARAMETER).
/// </summary>
public static class ParamBlockReader
{
    private static readonly HashSet<string> NumberTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int",
        "int32",
        "int64",
        "long",
        "short",
        "byte",
        "double",
        "single",
        "float",
        "decimal",
        "uint32",
        "uint64",
    };

    public static ParamBlockResult Read(string scriptText)
    {
        ArgumentNullException.ThrowIfNull(scriptText);
        var ast = Parser.ParseInput(scriptText, out _, out var errors);
        if (errors.Length > 0)
        {
            return new ParamBlockResult([], [.. errors.Select(e => $"Line {e.Extent.StartLineNumber}: {e.Message}")]);
        }

        if (ast.ParamBlock is not { } block)
        {
            return new ParamBlockResult([], ["The script has no param() block."]);
        }

        var help = ast.GetHelpContent();
        var problems = new List<string>();
        var parameters = new List<ScriptParameterDefinition>();
        foreach (var parameter in block.Parameters)
        {
            var name = parameter.Name.VariablePath.UserPath;
            if (name.StartsWith("SpecOps", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"${name}: names starting with 'SpecOps' are reserved, so it was skipped.");
                continue;
            }

            parameters.Add(ToDefinition(parameter, name, help, problems));
        }

        return new ParamBlockResult(parameters, problems);
    }

    private static ScriptParameterDefinition ToDefinition(
        ParameterAst parameter,
        string name,
        CommentHelpInfo? help,
        List<string> problems
    )
    {
        var definition = new ScriptParameterDefinition
        {
            Name = name,
            Label = Words(name) is var label && !string.Equals(label, name, StringComparison.Ordinal) ? label : null,
            Type = TypeFor(parameter.StaticType, name),
        };

        if (help is not null && help.Parameters.TryGetValue(name.ToUpperInvariant(), out var description))
        {
            definition.Description = OneLine(description);
        }

        foreach (var attribute in parameter.Attributes.OfType<AttributeAst>())
        {
            Apply(definition, attribute, problems);
        }

        if (parameter.DefaultValue is { } value)
        {
            if (Constant(value) is { } text)
            {
                definition.Default = definition.Type == ParameterType.Checkbox ? ToBool(text) : text;
            }
            else
            {
                problems.Add(
                    $"${name}: the default ({value.Extent.Text}) is computed when the script runs, so it isn't shown in the form."
                );
            }
        }

        if (definition.Type == ParameterType.Select && definition.Default is { } selected)
        {
            // Match ValidateSet's case-insensitivity, so the default is one of the options.
            definition.Default = definition
                .Options.FirstOrDefault(o => string.Equals(o.Value, selected, StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        return definition;
    }

    private static ParameterType TypeFor(Type type, string name)
    {
        if (type == typeof(PSCredential))
        {
            return ParameterType.Credential;
        }

        if (type == typeof(SwitchParameter) || type == typeof(bool))
        {
            return ParameterType.Checkbox;
        }

        if (
            type == typeof(System.Security.SecureString)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ParameterType.Password;
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return ParameterType.DateTime;
        }

        if (type == typeof(TimeSpan))
        {
            return ParameterType.Time;
        }

        if (NumberTypes.Contains(type.Name) || NumberTypes.Contains(Nullable.GetUnderlyingType(type)?.Name ?? ""))
        {
            return ParameterType.Number;
        }

        return name.Contains("email", StringComparison.OrdinalIgnoreCase) ? ParameterType.Email : ParameterType.Text;
    }

    private static void Apply(ScriptParameterDefinition definition, AttributeAst attribute, List<string> problems)
    {
        var positional = attribute.PositionalArguments;
        switch (attribute.TypeName.Name.ToUpperInvariant())
        {
            case "PARAMETER":
                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument.ArgumentName.Equals("Mandatory", StringComparison.OrdinalIgnoreCase))
                    {
                        // [Parameter(Mandatory)] has no value; [Parameter(Mandatory = $false)] does.
                        definition.Required =
                            argument.ExpressionOmitted
                            || !string.Equals(Constant(argument.Argument), "False", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (argument.ArgumentName.Equals("HelpMessage", StringComparison.OrdinalIgnoreCase))
                    {
                        definition.Description ??= Constant(argument.Argument);
                    }
                }

                break;

            case "VALIDATESET":
                if (definition.Type is ParameterType.Text or ParameterType.Email)
                {
                    definition.Type = ParameterType.Select;
                }

                foreach (var value in positional.Select(Constant).OfType<string>())
                {
                    definition.Options.Add(new SelectOption { Value = value });
                }

                break;

            case "VALIDATERANGE" when positional.Count == 2:
                definition.Min = Number(positional[0]);
                definition.Max = Number(positional[1]);
                if (definition.Type == ParameterType.Text)
                {
                    definition.Type = ParameterType.Number;
                }

                break;

            case "VALIDATEPATTERN" when positional.Count >= 1:
                definition.Pattern = Constant(positional[0]);
                break;

            case "VALIDATENOTNULLOREMPTY"
            or "VALIDATENOTNULL":
                definition.Required = true;
                break;

            case "VALIDATESCRIPT":
                problems.Add(
                    $"${definition.Name}: [ValidateScript] is checked by PowerShell when the script runs, not in the form."
                );
                break;

            default:
                break;
        }
    }

    /// <summary>The value of a constant expression ('text', 42, $true), or null if it has to be computed.</summary>
    private static string? Constant(ExpressionAst expression) =>
        expression switch
        {
            StringConstantExpressionAst s => s.Value,
            ConstantExpressionAst c => Convert.ToString(c.Value, CultureInfo.InvariantCulture),
            VariableExpressionAst v when v.VariablePath.UserPath.Equals("true", StringComparison.OrdinalIgnoreCase) =>
                "True",
            VariableExpressionAst v when v.VariablePath.UserPath.Equals("false", StringComparison.OrdinalIgnoreCase) =>
                "False",
            UnaryExpressionAst { TokenKind: TokenKind.Minus, Child: ConstantExpressionAst c } => "-"
                + Convert.ToString(c.Value, CultureInfo.InvariantCulture),
            _ => null,
        };

    private static double? Number(ExpressionAst expression) =>
        double.TryParse(Constant(expression), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string ToBool(string text) =>
        text.Equals("True", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "1", StringComparison.Ordinal)
            ? "true"
            : "false";

    /// <summary>"DelaySeconds" becomes "Delay seconds"; "VMName" becomes "VM name".</summary>
    private static string Words(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var startsWord =
                i > 0
                && char.IsUpper(c)
                && (
                    char.IsLower(name[i - 1])
                    || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))
                );
            if (startsWord)
            {
                builder.Append(' ');
            }

            // Keep acronyms (VM, ID) as they are; lower-case the start of later words.
            var inAcronym = char.IsUpper(c) && i + 1 < name.Length && char.IsUpper(name[i + 1]);
            builder.Append(startsWord && !inAcronym ? char.ToLowerInvariant(c) : c);
        }

        return builder.ToString().Replace('_', ' ');
    }

    private static string OneLine(string text) =>
        string.Join(
            ' ',
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );
}
