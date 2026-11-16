using Bunit;
using SpecOps.Core.Execution;
using SpecOps.Core.Scripts;
using SpecOps.Web.Components.Shared;

namespace SpecOps.Tests.Web;

/// <summary>bUnit tests for the reusable UI components.</summary>
public sealed class ComponentTests : IDisposable
{
    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    private IRenderedComponent<ParameterField> RenderField(
        ScriptParameterDefinition definition,
        string? value = null,
        string? error = null,
        Action<string?>? changed = null
    ) =>
        _context.Render<ParameterField>(p =>
            p.Add(x => x.Definition, definition)
                .Add(x => x.Value, value)
                .Add(x => x.Error, error)
                .Add(x => x.ValueChanged, v => changed?.Invoke(v))
        );

    [Fact]
    public void Text_field_carries_html_validation_attributes()
    {
        var cut = RenderField(
            new ScriptParameterDefinition
            {
                Name = "Code",
                Label = "Site code",
                Required = true,
                Pattern = "[A-Z]{3}",
                Placeholder = "ABC",
            }
        );

        var input = cut.Find("input#param-Code");
        Assert.True(input.HasAttribute("required"));
        Assert.Equal("[A-Z]{3}", input.GetAttribute("pattern"));
        Assert.Equal("ABC", input.GetAttribute("placeholder"));
        Assert.Contains("Site code", cut.Find("label").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_lists_its_options_with_labels()
    {
        var cut = RenderField(
            new ScriptParameterDefinition
            {
                Name = "Env",
                Type = ParameterType.Select,
                Options =
                {
                    new SelectOption { Value = "dev", Label = "Development" },
                    new SelectOption { Value = "prod" },
                },
            }
        );

        var options = cut.FindAll("option").Select(o => (o.GetAttribute("value"), o.TextContent)).ToList();
        Assert.Equal([("", "Select…"), ("dev", "Development"), ("prod", "prod")], options);
    }

    [Fact]
    public void Input_type_follows_the_parameter_type()
    {
        Assert.Equal(
            "datetime-local",
            RenderField(new() { Name = "When", Type = ParameterType.DateTime }).Find("input").GetAttribute("type")
        );
        Assert.Equal(
            "password",
            RenderField(new() { Name = "Secret", Type = ParameterType.Password }).Find("input").GetAttribute("type")
        );
    }

    [Fact]
    public void Shows_the_validation_error_and_marks_the_input()
    {
        var cut = RenderField(new ScriptParameterDefinition { Name = "Name" }, error: "Required.");

        Assert.Contains("Required.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("input-invalid", cut.Find("input").GetAttribute("class"), StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_are_reported_to_the_parent()
    {
        string? received = null;
        var cut = RenderField(new ScriptParameterDefinition { Name = "Name" }, changed: v => received = v);

        cut.Find("input").Change("hello");

        Assert.Equal("hello", received);
    }

    [Fact]
    public void Checkbox_reports_true_and_false()
    {
        string? received = null;
        var cut = RenderField(
            new ScriptParameterDefinition { Name = "Force", Type = ParameterType.Checkbox },
            changed: v => received = v
        );

        cut.Find("input[type=checkbox]").Change(true);
        Assert.Equal("true", received);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded, "Succeeded")]
    [InlineData(RunStatus.CompletedWithErrors, "Completed with errors")]
    [InlineData(RunStatus.TimedOut, "Timed out")]
    public void Status_badge_uses_friendly_text(RunStatus status, string text)
    {
        var cut = _context.Render<RunStatusBadge>(p => p.Add(x => x.Status, status));

        Assert.Equal(text, cut.Find("span").TextContent.Trim());
    }

    [Fact]
    public void Running_badge_pulses()
    {
        var cut = _context.Render<RunStatusBadge>(p => p.Add(x => x.Status, RunStatus.Running));

        Assert.NotNull(cut.Find(".animate-pulse"));
    }
}
