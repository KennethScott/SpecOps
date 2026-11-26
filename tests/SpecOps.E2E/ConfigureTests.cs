namespace SpecOps.E2E;

/// <summary>The Admin > Configure pages: scripts, their parameters and code, edited in the browser.</summary>
public sealed class ConfigureTests(SpecOpsApp app)
{
    [Fact]
    public async Task An_admin_can_create_a_script_with_code_and_run_it()
    {
        var page = await app.SignedInPageAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Configure" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "New script" }).ClickAsync();

        await page.GetByLabel("Id", new() { Exact = true }).FillAsync("e2e-greeting");
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("E2E greeting");
        await page.GetByLabel("File", new() { Exact = true }).FillAsync("e2e/Greeting.ps1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add parameter" }).ClickAsync();
        await page.GetByLabel("Parameter name").FillAsync("Who");

        await page.GetByRole(AriaRole.Tab, new() { Name = "Code" }).ClickAsync();
        var editor = page.Locator(".monaco-editor").First;
        await Expect(editor).ToBeVisibleAsync();
        // The starter template already declares the parameter.
        await Expect(editor).ToContainTextAsync("$Who");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Delete");
        await page.Keyboard.InsertTextAsync("param([string]$Who) \"Greetings, $Who\"");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved E2E greeting.");

        await page.GotoAsync("scripts/e2e-greeting");
        await page.GetByLabel("Who").FillAsync("Playwright");
        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();

        await Expect(page.GetByText("Succeeded", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Log)).ToContainTextAsync("Greetings, Playwright");
    }

    [Fact]
    public async Task Policy_and_history_pages_load()
    {
        var page = await app.SignedInPageAsync();

        await page.GotoAsync("admin/config/policies");
        await Expect(page.GetByLabel("Policy name").First).ToHaveValueAsync("Help desk");
        await page.GotoAsync("admin/config/history");
        await Expect(page.GetByRole(AriaRole.Cell, new() { Name = "Imported from specops.json" })).ToBeVisibleAsync();
    }
}
