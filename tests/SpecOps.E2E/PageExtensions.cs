namespace SpecOps.E2E;

public static class PageExtensions
{
    /// <summary>Opens an app page and waits until it's interactive (see <see cref="SpecOpsApp.WaitForInteractiveAsync"/>).</summary>
    public static async Task GotoInteractiveAsync(this IPage page, string url)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.GotoAsync(url);
        await SpecOpsApp.WaitForInteractiveAsync(page);
    }
}
