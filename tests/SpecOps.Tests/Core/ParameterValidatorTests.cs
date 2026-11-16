using SpecOps.Core.Scripts;
using SpecOps.Core.Validation;

namespace SpecOps.Tests.Core;

public class ParameterValidatorTests
{
    private static ParameterValidationResult Validate(ScriptParameterDefinition def, string? value) =>
        ParameterValidator.Validate([def], new Dictionary<string, string?> { [def.Name] = value });

    [Fact]
    public void Required_value_missing_is_an_error()
    {
        var result = Validate(new() { Name = "Name", Required = true }, "  ");

        Assert.False(result.IsValid);
        Assert.Equal("Required.", result.Errors["Name"]);
    }

    [Fact]
    public void Optional_empty_value_is_omitted()
    {
        var result = Validate(new() { Name = "Name" }, "");

        Assert.True(result.IsValid);
        Assert.Empty(result.Values);
    }

    [Fact]
    public void Unknown_and_reserved_parameters_are_rejected()
    {
        var result = ParameterValidator.Validate(
            [new ScriptParameterDefinition { Name = "Name" }],
            new Dictionary<string, string?>
            {
                ["Name"] = "x",
                ["SpecOpsCurrentUser"] = "spoofed",
                ["Other"] = "y",
            }
        );

        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("SpecOpsCurrentUser", result.Errors.Keys);
        Assert.Contains("Other", result.Errors.Keys);
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("ABC", true)]
    [InlineData("abcd", false)]
    [InlineData("ab1", false)]
    public void Pattern_must_match_the_whole_value(string value, bool valid)
    {
        var result = Validate(new() { Name = "Code", Pattern = "[A-Za-z]{3}" }, value);

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void Invalid_pattern_reports_a_configuration_error_instead_of_throwing()
    {
        var result = Validate(new() { Name = "Code", Pattern = "([" }, "x");

        Assert.Contains("invalid pattern", result.Errors["Code"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("2.5", 2.5)]
    public void Numbers_are_converted_to_int_when_whole(string value, object expected)
    {
        var result = Validate(new() { Name = "N", Type = ParameterType.Number }, value);

        Assert.Equal(expected, result.Values["N"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void Numbers_outside_range_or_invalid_are_rejected(string value)
    {
        var result = Validate(
            new()
            {
                Name = "N",
                Type = ParameterType.Range,
                Min = 1,
                Max = 10,
            },
            value
        );

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Select_only_accepts_configured_options()
    {
        var def = new ScriptParameterDefinition
        {
            Name = "Env",
            Type = ParameterType.Select,
            Options = [new() { Value = "dev" }, new() { Value = "prod" }],
        };

        Assert.True(Validate(def, "prod").IsValid);
        Assert.False(Validate(def, "staging").IsValid);
    }

    [Fact]
    public void Dates_times_and_checkboxes_are_typed()
    {
        var result = ParameterValidator.Validate(
            [
                new ScriptParameterDefinition { Name = "D", Type = ParameterType.Date },
                new ScriptParameterDefinition { Name = "T", Type = ParameterType.Time },
                new ScriptParameterDefinition { Name = "DT", Type = ParameterType.DateTime },
                new ScriptParameterDefinition { Name = "C", Type = ParameterType.Checkbox },
                new ScriptParameterDefinition { Name = "Unchecked", Type = ParameterType.Checkbox },
            ],
            new Dictionary<string, string?>
            {
                ["D"] = "2026-11-15",
                ["T"] = "13:45",
                ["DT"] = "2026-11-15T13:45",
                ["C"] = "true",
            }
        );

        Assert.True(result.IsValid);
        Assert.Equal(new DateTime(2026, 11, 15), result.Values["D"]);
        Assert.Equal(new TimeSpan(13, 45, 0), result.Values["T"]);
        Assert.Equal(new DateTime(2026, 11, 15, 13, 45, 0), result.Values["DT"]);
        Assert.Equal(true, result.Values["C"]);
        Assert.Equal(false, result.Values["Unchecked"]);
    }

    [Theory]
    [InlineData("someone@example.com", true)]
    [InlineData("not-an-email", false)]
    [InlineData("Someone <someone@example.com>", false)]
    public void Email_must_be_a_plain_address(string value, bool valid)
    {
        Assert.Equal(valid, Validate(new() { Name = "E", Type = ParameterType.Email }, value).IsValid);
    }

    [Fact]
    public void Passwords_are_not_trimmed()
    {
        var result = Validate(new() { Name = "P", Type = ParameterType.Password }, " secret ");

        Assert.Equal(" secret ", result.Values["P"]);
    }
}
