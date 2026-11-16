using Microsoft.JSInterop;

namespace SpecOps.Web.Infrastructure;

public static class JSRuntimeExtensions
{
    /// <summary>JS interop that quietly does nothing when the browser has gone away (circuit disconnected, page closed).</summary>
    public static async ValueTask TryInvokeVoidAsync(this IJSRuntime js, string identifier, params object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(js);
        try
        {
            await js.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    public static async ValueTask<T?> TryInvokeAsync<T>(this IJSRuntime js, string identifier, params object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(js);
        try
        {
            return await js.InvokeAsync<T>(identifier, args);
        }
        catch (JSDisconnectedException)
        {
            return default;
        }
        catch (TaskCanceledException)
        {
            return default;
        }
    }
}
