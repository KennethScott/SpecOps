using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using SpecOps.Core.Scripts;

namespace SpecOps.Core.Validation;

public sealed record ParameterValidationResult(
    IReadOnlyDictionary<string, object> Values,
    IReadOnlyDictionary<string, string> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Server-side validation and type conversion of form input against a script's parameter definitions.
/// The browser's HTML validation is a convenience only; this is the gate.
/// </summary>
public static class ParameterValidator
{
    public const int MaxLength = 10_000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static ParameterValidationResult Validate(
        IEnumerable<ScriptParameterDefinition> definitions,
        IReadOnlyDictionary<string, string?> input
    )
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(input);

        var defs = definitions.ToList();
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (
            var key in input.Keys.Where(k =>
                !defs.Exists(d => string.Equals(d.Name, k, StringComparison.OrdinalIgnoreCase))
            )
        )
        {
            errors[key] = "Unknown parameter.";
        }

        foreach (var def in defs)
        {
            input.TryGetValue(def.Name, out var raw);
            if (raw is not null && def.Type is not (ParameterType.Password or ParameterType.TextArea))
            {
                raw = raw.Trim();
            }

            if (string.IsNullOrEmpty(raw))
            {
                if (def.Type == ParameterType.Checkbox)
                {
                    values[def.Name] = false;
                }
                else if (def.Required)
                {
                    errors[def.Name] = "Required.";
                }

                continue;
            }

            if (raw.Length > MaxLength)
            {
                errors[def.Name] = $"Must be {MaxLength:N0} characters or fewer.";
                continue;
            }

            var (value, error) = Convert(def, raw);
            if (error is not null)
            {
                errors[def.Name] = error;
            }
            else
            {
                values[def.Name] = value!;
            }
        }

        return new ParameterValidationResult(values, errors);
    }

    private static (object? Value, string? Error) Convert(ScriptParameterDefinition def, string raw)
    {
        switch (def.Type)
        {
            case ParameterType.Text:
            case ParameterType.TextArea:
            case ParameterType.Password:
                return MatchesPattern(def, raw) is { } patternError ? (null, patternError) : (raw, null);

            case ParameterType.Email:
                if (!MailAddress.TryCreate(raw, out var address) || address.Address != raw)
                {
                    return (null, "Enter a valid email address.");
                }

                return MatchesPattern(def, raw) is { } emailPatternError ? (null, emailPatternError) : (raw, null);

            case ParameterType.Select:
                return def.Options.Any(o => o.Value == raw) ? (raw, null) : (null, "Choose one of the listed options.");

            case ParameterType.Number:
            case ParameterType.Range:
                if (
                    !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || !double.IsFinite(number)
                )
                {
                    return (null, "Enter a number.");
                }

                if (def.Min is { } min && number < min)
                {
                    return (null, $"Must be at least {min.ToString(CultureInfo.InvariantCulture)}.");
                }

                if (def.Max is { } max && number > max)
                {
                    return (null, $"Must be at most {max.ToString(CultureInfo.InvariantCulture)}.");
                }

                return number % 1 == 0 && number is >= int.MinValue and <= int.MaxValue
                    ? ((int)number, null)
                    : (number, null);

            case ParameterType.Date:
                return DateOnly.TryParseExact(
                    raw,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date
                )
                    ? (date.ToDateTime(TimeOnly.MinValue), null)
                    : (null, "Enter a valid date.");

            case ParameterType.Time:
                return TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    ? (time.ToTimeSpan(), null)
                    : (null, "Enter a valid time.");

            case ParameterType.DateTime:
                return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime)
                    ? (dateTime, null)
                    : (null, "Enter a valid date and time.");

            case ParameterType.Checkbox:
                if (bool.TryParse(raw, out var flag))
                {
                    return (flag, null);
                }

                return string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase)
                    ? (true, null)
                    : (null, "Must be true or false.");

            default:
                return (null, $"Unsupported parameter type '{def.Type}'.");
        }
    }

    private static string? MatchesPattern(ScriptParameterDefinition def, string raw)
    {
        if (string.IsNullOrEmpty(def.Pattern))
        {
            return null;
        }

        try
        {
            return Regex.IsMatch(raw, $"^(?:{def.Pattern})$", RegexOptions.CultureInvariant, RegexTimeout)
                ? null
                : "Doesn't match the required format.";
        }
        catch (ArgumentException)
        {
            return "This parameter has an invalid pattern configured; contact an administrator.";
        }
        catch (RegexMatchTimeoutException)
        {
            return "Couldn't validate the value; try a shorter value.";
        }
    }
}
