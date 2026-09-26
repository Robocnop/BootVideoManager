using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.X11.Interop;

namespace BootVideoManager.App.Services;

/// <summary>
/// Reads the cookies of an embedded WebKitGTK view. Avalonia's WebView only exposes a cookie manager for its WPE
/// backend, while Linux desktops (and the GNOME Flatpak runtime) provide WebKitGTK: ask WebKit directly, on the GLib
/// thread that owns the view. The API returns HttpOnly cookies too, which is what the site session uses.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class WebKitGtkCookies
{
    private const string WebKit = "libwebkit2gtk-4.1.so.0";
    private const string Soup = "libsoup-3.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    /// <param name="webKitWebView">The <c>WebKitWebView*</c> behind the Avalonia WebView.</param>
    /// <param name="uri">Only the cookies that would be sent to this address are returned.</param>
    public static async Task<IReadOnlyList<Cookie>> GetAsync(nint webKitWebView, Uri uri)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<Cookie>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(completion);
        try
        {
            await GtkInteropHelper.RunOnGlibThread(() => Start(webKitWebView, uri.AbsoluteUri, GCHandle.ToIntPtr(handle)));
        }
        catch
        {
            handle.Free();
            throw;
        }

        return await completion.Task;
    }

    private static unsafe int Start(nint webView, string uri, nint userData)
    {
        var manager = webkit_web_context_get_cookie_manager(webkit_web_view_get_context(webView));
        webkit_cookie_manager_get_cookies(manager, uri, 0, &OnCookies, userData);
        return 0;
    }

    /// <summary>GAsyncReadyCallback, called on the GLib thread with a GList of SoupCookie owned by the caller.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnCookies(nint source, nint result, nint userData)
    {
        var handle = GCHandle.FromIntPtr(userData);
        var completion = (TaskCompletionSource<IReadOnlyList<Cookie>>)handle.Target!;
        handle.Free();
        try
        {
            nint error = 0;
            var list = webkit_cookie_manager_get_cookies_finish(source, result, &error);
            if (error != 0)
            {
                g_error_free(error);
                completion.TrySetException(new InvalidOperationException("WebKitGTK could not read its cookies."));
                return;
            }

            var cookies = new List<Cookie>();
            for (var node = list; node != 0; node = ((nint*)node)[1])
            {
                var soupCookie = ((nint*)node)[0];
                try
                {
                    var cookie = new Cookie(
                        Marshal.PtrToStringUTF8(soup_cookie_get_name(soupCookie)) ?? string.Empty,
                        Marshal.PtrToStringUTF8(soup_cookie_get_value(soupCookie)) ?? string.Empty,
                        Marshal.PtrToStringUTF8(soup_cookie_get_path(soupCookie)) ?? "/",
                        Marshal.PtrToStringUTF8(soup_cookie_get_domain(soupCookie)) ?? string.Empty);
                    if (soup_cookie_get_expires(soupCookie) is var expires and not 0)
                    {
                        cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(g_date_time_to_unix(expires)).UtcDateTime;
                    }

                    cookies.Add(cookie);
                }
                catch (CookieException)
                {
                    // A cookie System.Net cannot represent is not one the site session needs.
                }
                finally
                {
                    soup_cookie_free(soupCookie);
                }
            }

            if (list != 0)
            {
                g_list_free(list);
            }

            completion.TrySetResult(cookies);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    [LibraryImport(WebKit)]
    private static partial nint webkit_web_view_get_context(nint webView);

    [LibraryImport(WebKit)]
    private static partial nint webkit_web_context_get_cookie_manager(nint context);

    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial void webkit_cookie_manager_get_cookies(
        nint cookieManager, string uri, nint cancellable, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback, nint userData);

    [LibraryImport(WebKit)]
    private static unsafe partial nint webkit_cookie_manager_get_cookies_finish(nint cookieManager, nint result, nint* error);

    [LibraryImport(Soup)]
    private static partial nint soup_cookie_get_name(nint cookie);

    [LibraryImport(Soup)]
    private static partial nint soup_cookie_get_value(nint cookie);

    [LibraryImport(Soup)]
    private static partial nint soup_cookie_get_domain(nint cookie);

    [LibraryImport(Soup)]
    private static partial nint soup_cookie_get_path(nint cookie);

    [LibraryImport(Soup)]
    private static partial nint soup_cookie_get_expires(nint cookie);

    [LibraryImport(Soup)]
    private static partial void soup_cookie_free(nint cookie);

    [LibraryImport(GLib)]
    private static partial long g_date_time_to_unix(nint dateTime);

    [LibraryImport(GLib)]
    private static partial void g_list_free(nint list);

    [LibraryImport(GLib)]
    private static partial void g_error_free(nint error);
}
