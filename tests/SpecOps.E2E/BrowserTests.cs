namespace SpecOps.E2E;

/// <summary>The pages that lean on the browser code in src/SpecOps.Web/Client (TypeScript).</summary>
public sealed class BrowserTests(SpecOpsApp app)
{
    [Fact]
    public async Task Pages_require_sign_in()
    {
        var page = await app.NewPageAsync();

        await page.GotoAsync("terminal");

        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/account/login"));
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign in as dev user" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Terminal_runs_code_from_the_editor_and_shows_objects_as_results()
    {
        var page = await app.SignedInPageAsync();
        await page.GotoInteractiveAsync("terminal");

        // Monaco (lazy-loaded ES module bundle) replaces the loading placeholder.
        var editor = page.Locator(".monaco-editor").First;
        await Expect(editor).ToBeVisibleAsync();
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Delete");
        // InsertText bypasses key handling, so Monaco doesn't auto-close quotes and brackets (or wrap a selection).
        await page.Keyboard.InsertTextAsync(
            "[pscustomobject]@{ Name = 'alpha'; Size = 1 }, [pscustomobject]@{ Name = 'beta'; Size = 2 }"
        );

        // Ctrl/Cmd+Enter is wired up in the editor module and calls back into .NET.
        await page.Keyboard.PressAsync("ControlOrMeta+Enter");

        await Expect(page.GetByText("Succeeded", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Tab, new() { Name = "Results" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Columnheader, new() { Name = "Name" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Cell, new() { Name = "alpha", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Cell, new() { Name = "beta", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_heading_gets_focus_after_navigation_without_an_outline()
    {
        var page = await app.SignedInPageAsync();
        await page.GotoInteractiveAsync("runs");

        // Blazor focuses the h1 for screen readers (FocusOnNavigate); it shouldn't look like a focused control.
        var heading = page.GetByRole(AriaRole.Heading, new() { Name = "Runs", Exact = true });
        await Expect(heading).ToBeFocusedAsync();
        await Expect(heading).ToHaveCSSAsync("outline-style", "none");
    }

    [Fact]
    public async Task Theme_toggle_switches_theme_and_remembers_it()
    {
        var page = await app.SignedInPageAsync();
        var html = page.Locator("html");
        var startedDark = await html.EvaluateAsync<bool>("e => e.classList.contains('dark')");

        await page.GetByTitle("Toggle light/dark theme").ClickAsync();
        await Expect(html).ToHaveClassAsync(Dark(!startedDark));

        await page.ReloadAsync();
        await Expect(html).ToHaveClassAsync(Dark(!startedDark));
    }

    [Fact]
    public async Task Pages_load_without_script_errors_or_CSP_violations()
    {
        var page = await app.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => errors.Add(error);

        await page.GotoAsync("account/login");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in as dev user" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Scripts", Exact = true })).ToBeVisibleAsync();
        await page.GotoInteractiveAsync("terminal");
        await Expect(page.Locator(".monaco-editor").First).ToBeVisibleAsync();
        await page.GotoInteractiveAsync("admin/users");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Users" })).ToBeVisibleAsync();
        await page.GotoInteractiveAsync("admin/logs");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Logs" })).ToBeVisibleAsync();

        Assert.Empty(errors);
    }

    // "dark" as a whole class name (not e.g. "dark:bg-…"), present or absent.
    private static System.Text.RegularExpressions.Regex Dark(bool dark) =>
        new(dark ? @"(^|\s)dark(\s|$)" : @"^(?!.*(^|\s)dark(\s|$))");
}
