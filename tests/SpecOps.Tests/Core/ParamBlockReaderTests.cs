using SpecOps.Core.Scripts;

namespace SpecOps.Tests.Core;

public sealed class ParamBlockReaderTests
{
    private const string Script = """
        <#
        .SYNOPSIS
            Resets a password.
        .PARAMETER UserName
            The account to reset,
            e.g. jdoe.
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory, HelpMessage = 'Ignored: comment help wins')]
            [string]$UserName,

            [Parameter(Mandatory = $false)]
            [securestring]$NewPassword,

            [ValidateSet('dev', 'Test', 'prod')]
            [string]$Environment = 'test',

            [ValidateRange(1, 10)]
            [int]$Count = 3,

            [ValidatePattern('^[A-Z]{2}\d+$')]
            [string]$TicketId,

            [switch]$Force,

            [datetime]$When = (Get-Date),

            [string]$ManagerEmail,

            [string]$VMName,

            [int]$SpecOpsInternal
        )
        """;

    private static ScriptParameterDefinition Find(ParamBlockResult result, string name) =>
        Assert.Single(result.Parameters, p => p.Name == name);

    [Fact]
    public void Reads_each_parameter_as_a_form_field()
    {
        var result = ParamBlockReader.Read(Script);

        Assert.Equal(
            ["UserName", "NewPassword", "Environment", "Count", "TicketId", "Force", "When", "ManagerEmail", "VMName"],
            result.Parameters.Select(p => p.Name)
        );

        var user = Find(result, "UserName");
        Assert.True(user.Required);
        Assert.Equal(ParameterType.Text, user.Type);
        Assert.Equal("The account to reset, e.g. jdoe.", user.Description);
        Assert.Equal("User name", user.Label);

        var password = Find(result, "NewPassword");
        Assert.Equal(ParameterType.Password, password.Type);
        Assert.False(password.Required);

        var environment = Find(result, "Environment");
        Assert.Equal(ParameterType.Select, environment.Type);
        Assert.Equal(["dev", "Test", "prod"], environment.Options.Select(o => o.Value));
        Assert.Equal("Test", environment.Default);

        var count = Find(result, "Count");
        Assert.Equal(ParameterType.Number, count.Type);
        Assert.Equal((1, 10), (count.Min, count.Max));
        Assert.Equal("3", count.Default);

        Assert.Equal(@"^[A-Z]{2}\d+$", Find(result, "TicketId").Pattern);
        Assert.Equal(ParameterType.Checkbox, Find(result, "Force").Type);
        Assert.Equal(ParameterType.Email, Find(result, "ManagerEmail").Type);
        Assert.Equal("VM name", Find(result, "VMName").Label);

        var when = Find(result, "When");
        Assert.Equal(ParameterType.DateTime, when.Type);
        Assert.Null(when.Default);
    }

    [Fact]
    public void Explains_what_it_skipped()
    {
        var result = ParamBlockReader.Read(Script);

        Assert.Contains(result.Problems, p => p.Contains("$When", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("$SpecOpsInternal", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_syntax_errors_and_missing_param_blocks_without_running_anything()
    {
        Assert.Contains(
            "Line 1",
            Assert.Single(ParamBlockReader.Read("param([string]$x").Problems),
            StringComparison.Ordinal
        );
        Assert.Empty(ParamBlockReader.Read("Write-Output 'hi'").Parameters);
        // Defaults are read, never evaluated.
        Assert.Null(Assert.Single(ParamBlockReader.Read("param($x = (Remove-Item C:\\ -Recurse))").Parameters).Default);
    }

    [Fact]
    public void The_sample_demo_script_reads_cleanly()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "samples",
            "scripts",
            "Demo.ps1"
        );

        var result = ParamBlockReader.Read(File.ReadAllText(path));

        Assert.Equal(8, result.Parameters.Count);
        Assert.Empty(result.Problems);
    }
}
