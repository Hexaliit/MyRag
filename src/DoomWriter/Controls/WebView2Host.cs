using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace DoomWriter.Controls;

/// <summary>
/// Hosts a WebView2 (Edge Chromium) control inside Avalonia via NativeControlHost.
/// Windows-only. Falls back to a message if WebView2 runtime is not installed.
/// </summary>
public class WebView2Host : NativeControlHost, IDisposable
{
    private Microsoft.Web.WebView2.Core.CoreWebView2Controller? _controller;
    private Microsoft.Web.WebView2.Core.CoreWebView2? _webView;
    private IntPtr _hostHwnd;

    /// <summary>Fires when the CoreWebView2 is created and ready to navigate.</summary>
    public event EventHandler? WebViewReady;

    /// <summary>Fires when page navigation completes.</summary>
    public event EventHandler? NavigationCompleted;

    /// <summary>Fires when JS sends a message via chrome.webview.postMessage.</summary>
    public event EventHandler<string>? WebMessageReceived;

    /// <summary>Fires if WebView2 runtime is not available.</summary>
    public event EventHandler<string>? InitializationFailed;

    public bool IsReady => _webView != null;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _hostHwnd = handle.Handle;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = InitWebViewAsync());
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _controller?.Close();
        _controller = null;
        _webView = null;
        base.DestroyNativeControlCore(control);
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DoomWriter", "webview2-data");
            Directory.CreateDirectory(userDataFolder);

            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataFolder);

            _controller = await env.CreateCoreWebView2ControllerAsync(_hostHwnd);
            _webView = _controller.CoreWebView2;

            // Configure browser settings
            _webView.Settings.IsStatusBarEnabled = false;
            _webView.Settings.AreDefaultContextMenusEnabled = true;
            _webView.Settings.IsZoomControlEnabled = false;
#if DEBUG
            _webView.Settings.AreDevToolsEnabled = true;
#else
            _webView.Settings.AreDevToolsEnabled = false;
#endif

            // Wire message bridge (JS → C#)
            _webView.WebMessageReceived += (s, e) =>
            {
                // postMessage sends a string; TryGetWebMessageAsString returns it directly
                var msg = e.TryGetWebMessageAsString();
                if (msg != null)
                    WebMessageReceived?.Invoke(this, msg);
            };

            // Wire navigation completed
            _webView.NavigationCompleted += (s, e) =>
            {
                NavigationCompleted?.Invoke(this, EventArgs.Empty);
            };

            // Debug: log console messages from the WebView
            _webView.ConsoleMessageReceived += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[WebView2 Console] {e.Message}");
            };

            // Set initial bounds
            UpdateBounds();

            WebViewReady?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex}");
            InitializationFailed?.Invoke(this,
                $"WebView2 runtime not available: {ex.Message}. Install from https://developer.microsoft.com/en-us/microsoft-edge/webview2/");
        }
    }

    /// <summary>Navigate to a URL.</summary>
    public void Navigate(string url)
    {
        _webView?.Navigate(url);
    }

    /// <summary>Navigate to an HTML string directly.</summary>
    public void NavigateToString(string html)
    {
        _webView?.NavigateToString(html);
    }

    /// <summary>
    /// Execute JavaScript and return the result.
    /// Results are JSON-decoded: strings are unquoted, null/undefined return null.
    /// </summary>
    public async Task<string?> ExecuteScriptAsync(string script)
    {
        if (_webView == null) return null;

        var result = await _webView.ExecuteScriptAsync(script);
        if (result == null) return null;

        // ExecuteScriptAsync returns JSON-encoded results
        if (result is "null" or "undefined")
            return null;

        // String results are JSON-quoted: "\"hello\"" → "hello"
        if (result.StartsWith('"') && result.EndsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(result); }
            catch { return result; }
        }

        return result;
    }

    /// <summary>
    /// Execute JavaScript without caring about the return value.
    /// Faster than ExecuteScriptAsync since it doesn't wait for results.
    /// </summary>
    public void PostScript(string script)
    {
        _webView?.ExecuteScriptAsync(script);
    }

    private void UpdateBounds()
    {
        if (_controller == null) return;

        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var w = (int)(Bounds.Width * scaling);
        var h = (int)(Bounds.Height * scaling);

        if (w > 0 && h > 0)
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, w, h);
            _controller.IsVisible = true;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateBounds();
        return result;
    }

    public void Dispose()
    {
        _controller?.Close();
        _controller = null;
        _webView = null;
        GC.SuppressFinalize(this);
    }
}
