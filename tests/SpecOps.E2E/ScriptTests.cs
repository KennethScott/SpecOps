using System.Text.RegularExpressions;

namespace SpecOps.E2E;

public sealed partial class ScriptTests(SpecOpsApp app)
{
    [Fact]
    public async Task Signing_in_shows_the_script_catalog_and_search_filters_it()
    {
        var page = await app.SignedInPageAsync();

        var systemInfo = page.Locator("a[href='scripts/system-info']");
        var demo = page.Locator("a[href='scripts/demo']");
        await Expect(systemInfo).ToContainTextAsync("System info");
        await Expect(demo).ToContainTextAsync("Demo");

        await page.GetByLabel("Search scripts").FillAsync("system");

        await Expect(systemInfo).ToBeVisibleAsync();
        await Expect(demo).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Running_a_script_streams_its_output_until_it_succeeds()
    {
        var page = await app.SignedInPageAsync();
        await page.GotoAsync("scripts/system-info");

        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();

        await Expect(page.GetByText("Succeeded", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Log)).ToContainTextAsync("PowerShell");
        await Expect(page.GetByRole(AriaRole.Log)).ToContainTextAsync(".NET");
    }

    [Fact]
    public async Task Parameters_are_passed_to_the_script_and_a_run_can_be_cancelled()
    {
        var page = await app.SignedInPageAsync();
        await page.GotoAsync("scripts/demo");

        await page.GetByLabel("Message").FillAsync("Hello from Playwright");
        await page.GetByLabel("Delay (seconds)").FillAsync("60");
        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();

        // The parameter value arrives in the output; the script then sleeps, so cancel it.
        await Expect(page.GetByRole(AriaRole.Log)).ToContainTextAsync("Hello from Playwright");
        await Expect(page.GetByRole(AriaRole.Log)).ToContainTextAsync("Sleeping 60 seconds");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        await Expect(page.GetByText("Cancelled", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_run_can_be_reopened_from_its_own_page()
    {
        var page = await app.SignedInPageAsync();
        await page.GotoAsync("scripts/system-info");
        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Succeeded", new() { Exact = true })).ToBeVisibleAsync();

        await page.Locator("a[href^='runs/']").ClickAsync();

        await Expect(page).ToHaveURLAsync(RunUrl());
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "System info" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Log)).ToContainTextAsync("PowerShell");
    }

    [GeneratedRegex("/runs/[0-9a-f-]{36}$")]
    private static partial Regex RunUrl();
}
