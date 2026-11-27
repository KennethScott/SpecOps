namespace SpecOps.E2E;

/// <summary>The Admin > Configure pages: scripts, their parameters and code, edited in the browser.</summary>
public sealed class ConfigureTests(SpecOpsApp app)
{
    [Fact]
    public async Task An_admin_can_create_a_script_with_code_and_run_it()
    {
        var page = await app.SignedInPageAsync();
        await app.WithDiagnosticsAsync(page, async () =>
        {
            await page.GetByRole(AriaRole.Link, new() { Name = "Configure" }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "New script" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/config/scripts/new$"));

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

            await page.GotoInteractiveAsync("scripts/e2e-greeting");
            await page.GetByLabel("Who").FillAsync("Playwright");
            await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();

            await Expect(page.GetByText("Succeeded", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Log)).ToContainTextAsync("Greetings, Playwright");
        });
    }

    [Fact]
    public async Task A_stored_credential_reaches_the_script_as_a_PSCredential()
    {
        var page = await app.SignedInPageAsync();
        await page.GotoInteractiveAsync("admin/config/credentials");
        await app.WithDiagnosticsAsync(page, async () =>
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Add credential" }).ClickAsync();
            await page.GetByLabel("Credential id").FillAsync("e2e-account");
            await page.GetByLabel("Credential name").FillAsync("E2E account");
            await page.GetByLabel("User name").FillAsync("e2e-user");
            await page.GetByLabel("Password").FillAsync("Pa55word!");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved 'E2E account'.");
            // The password is never sent back to the browser.
            await Expect(page.GetByLabel("Password")).ToHaveValueAsync("");

            await page.GotoInteractiveAsync("admin/config/scripts/new");
            await page.GetByLabel("Id", new() { Exact = true }).FillAsync("e2e-credential");
            await page.GetByLabel("Name", new() { Exact = true }).FillAsync("E2E credential");
            await page.GetByLabel("File", new() { Exact = true }).FillAsync("e2e/Credential.ps1");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add parameter" }).ClickAsync();
            await page.GetByLabel("Parameter name").FillAsync("Account");
            await page.GetByLabel("Parameter type").SelectOptionAsync("Credential");
            await page.GetByLabel("E2E account").CheckAsync();

            await page.GetByRole(AriaRole.Tab, new() { Name = "Code" }).ClickAsync();
            var editor = page.Locator(".monaco-editor").First;
            await Expect(editor).ToContainTextAsync("[pscredential]$Account");
            await editor.ClickAsync();
            await page.Keyboard.PressAsync("ControlOrMeta+A");
            await page.Keyboard.PressAsync("Delete");
            await page.Keyboard.InsertTextAsync(
                "param([pscredential]$Account) \"$($Account.UserName) has a $($Account.GetNetworkCredential().Password.Length)-character password\""
            );
            await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved E2E credential.");

            // Only one credential allowed, so the form just says which one.
            await page.GotoInteractiveAsync("scripts/e2e-credential");
            await Expect(page.Locator("#param-Account")).ToHaveTextAsync("E2E account");
            await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();

            await Expect(page.GetByText("Succeeded", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Log)).ToContainTextAsync("e2e-user has a 9-character password");
        });
    }

    [Fact]
    public async Task Script_files_can_be_uploaded_into_a_folder()
    {
        var page = await app.SignedInPageAsync();
        await page.GotoInteractiveAsync("admin/config/files");

        await app.WithDiagnosticsAsync(page, async () =>
        {
            await page.GetByLabel("Into folder").FillAsync("uploads");
            await page.GetByLabel("Files to upload")
                .SetInputFilesAsync(
                    new FilePayload
                    {
                        Name = "Uploaded.ps1",
                        MimeType = "text/plain",
                        Buffer = System.Text.Encoding.UTF8.GetBytes("Write-Output 'uploaded'"),
                    }
                );

            await Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Uploaded uploads/Uploaded.ps1.");
            await Expect(page.GetByRole(AriaRole.Cell, new() { Name = "uploads/Uploaded.ps1", Exact = true }))
                .ToBeVisibleAsync();
        });
    }

    [Fact]
    public async Task Policy_and_history_pages_load()
    {
        var page = await app.SignedInPageAsync();

        // The section's start page is the Scripts tab.
        await page.GotoInteractiveAsync("admin/config");
        var sections = page.GetByRole(AriaRole.Navigation, new() { Name = "Configuration sections" });
        await Expect(sections.GetByRole(AriaRole.Link, new() { Name = "Scripts" }))
            .ToHaveAttributeAsync("aria-current", "page");

        await page.GotoInteractiveAsync("admin/config/policies");
        await Expect(sections.GetByRole(AriaRole.Link, new() { Name = "Policies" }))
            .ToHaveAttributeAsync("aria-current", "page");
        await Expect(page.GetByLabel("Policy name").First).ToHaveValueAsync("Help desk");
        await page.GotoInteractiveAsync("admin/config/history");
        await Expect(page.GetByRole(AriaRole.Cell, new() { Name = "Imported from specops.json" })).ToBeVisibleAsync();
    }
}
