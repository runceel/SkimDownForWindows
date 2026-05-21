using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using SkimDownForWindows.Models;
namespace SkimDownForWindows.Viewer;

/// <summary>
/// WebView2-hosted Markdown renderer.
///
/// Security model (per critique):
///   - Two distinct virtual host names:
///       <c>https://skimdown-app.example/</c>      -> bundled web assets (HTML/CSS/JS)
///       <c>https://skimdown-content.example/</c>  -> the opened folder (relative images only)
///   - Content host is remapped each time a folder is opened.
///   - Content is passed via <c>PostWebMessageAsJson</c>; we never use
///     <c>NavigateToString</c> for the document body.
/// </summary>
public sealed partial class MarkdownPreview : UserControl
{
    private const string AppHost = "skimdown-app.example";
    private const string ContentHost = "skimdown-content.example";

    public event Action<string>? RelativeMarkdownLinkClicked;
    public event Action<string>? ExternalLinkClicked;
    public event Action<int, int>? SearchResult; // (total, currentOneBased)

    private bool _initialized;
    private bool _webReady;
    private string? _pendingMarkdown;
    private string? _pendingRelativePath;
    private string? _pendingTheme;
    private string? _currentFolderRoot;

    public MarkdownPreview()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initialize WebView2 + set up the dual virtual host mappings.
    /// Must be awaited before any <see cref="LoadAsync"/>.
    /// </summary>
    public async Task InitializeAsync(string appWebFolderAbsolutePath)
    {
        if (_initialized)
        {
            return;
        }

        await Web.EnsureCoreWebView2Async();
        var core = Web.CoreWebView2;

        // App-asset host: read-only access to the bundled web folder.
        core.SetVirtualHostNameToFolderMapping(
            AppHost,
            appWebFolderAbsolutePath,
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += (s, e) =>
        {
            // Any window.open / target=_blank -> route via our external-link path.
            e.Handled = true;
            ExternalLinkClicked?.Invoke(e.Uri);
        };

        core.NavigationCompleted += (s, e) =>
        {
            if (!e.IsSuccess)
            {
                LogToFile($"WebView2 NavigationCompleted failed: {e.WebErrorStatus} appFolder={appWebFolderAbsolutePath}");
            }
        };

        // Disable dev defaults that are noisy for an end-user reader.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        // Make the WebView2's surface transparent so when the renderer hasn't
        // pushed body styles yet, the parent (themed) Border shows through —
        // no white flash on first paint, no white margin where the body is
        // shorter than the viewport.
        try
        {
            Web.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        }
        catch { /* not all WebView2 builds expose this */ }

        _initialized = true;

        var indexUri = $"https://{AppHost}/renderer.html";
        Web.Source = new Uri(indexUri);
    }

    /// <summary>
    /// Set the WebView2 host-surface background color used before the
    /// rendered document paints its own background. Transparent stays in
    /// effect except during initial load; the renderer's own body background
    /// covers everything once <see cref="LoadAsync"/> has run.
    /// </summary>
    public void SetHostBackground(Windows.UI.Color color)
    {
        if (!_initialized) return;
        try { Web.DefaultBackgroundColor = color; }
        catch { /* best-effort */ }
    }

    private static void LogToFile(string msg)
    {
        // Logs only errors / unusual events. Quiet during normal operation.
        try
        {
            var logDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logPath = Path.Combine(logDir, "SkimDownForWindows-web.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Tell WebView2 to serve <paramref name="folderRoot"/> at the content virtual host.
    /// Must be called before <see cref="LoadAsync"/> when the open folder changes.
    /// </summary>
    public void SetContentFolder(string folderRoot)
    {
        if (!_initialized) return;
        if (string.IsNullOrEmpty(folderRoot)) return;

        var core = Web.CoreWebView2;
        // Clear any existing mapping for the host (idempotent for first call).
        try { core.ClearVirtualHostNameToFolderMapping(ContentHost); }
        catch { /* no mapping yet */ }

        core.SetVirtualHostNameToFolderMapping(
            ContentHost,
            folderRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        _currentFolderRoot = folderRoot;
    }

    /// <summary>
    /// Render the provided Markdown. <paramref name="relativePath"/> is forward-slash
    /// relative to the content host, used to resolve relative images.
    /// </summary>
    public Task LoadAsync(string markdown, string relativePath, string theme)
    {
        _pendingMarkdown = markdown;
        _pendingRelativePath = relativePath;
        _pendingTheme = theme;
        return FlushPendingAsync();
    }

    public void ShowEmpty()
    {
        if (_webReady)
        {
            Post(new { type = "empty" });
        }
    }

    public void SetTheme(string theme)
    {
        _pendingTheme = theme;
        if (_webReady)
        {
            Post(new { type = "theme", theme });
        }
    }

    public void SetZoom(double factor)
    {
        if (!_webReady) return;
        Post(new { type = "zoom", factor });
    }

    public void Search(string query, bool caseSensitive)
    {
        if (!_webReady) return;
        Post(new { type = "search", query, caseSensitive });
    }

    public void SearchNext() { if (_webReady) Post(new { type = "search/next" }); }
    public void SearchPrev() { if (_webReady) Post(new { type = "search/prev" }); }
    public void SearchClear() { if (_webReady) Post(new { type = "search/clear" }); }

    /// <summary>Ask the renderer to select all body text inside the preview.</summary>
    public void SelectAll()
    {
        if (_webReady) Post(new { type = "selectAll" });
    }

    /// <summary>Ask the renderer to post the current selection back as a "copy" message.</summary>
    public void CopySelection()
    {
        if (_webReady) Post(new { type = "copySelection" });
    }

    /// <summary>Ask the renderer to smooth-scroll to a #hash anchor (slug-aware).</summary>
    public void ScrollToAnchor(string hash)
    {
        if (_webReady) Post(new { type = "scrollToAnchor", hash = hash ?? "" });
    }

    /// <summary>
    /// Return the current text selection inside the WebView2, or <c>null</c> if
    /// nothing is selected / the WebView isn't ready. Used by the
    /// <c>Edit &gt; Use Selection for Find</c> menu (Ctrl+E).
    /// </summary>
    public async Task<string?> GetSelectedTextAsync()
    {
        if (!_initialized || Web.CoreWebView2 is null) return null;
        try
        {
            // ExecuteScriptAsync returns a JSON-encoded value (so a JS string
            // comes back as a JSON-quoted string). Parse it through JsonDocument.
            var raw = await Web.CoreWebView2.ExecuteScriptAsync(
                "(function(){try{return (window.getSelection()||'').toString();}catch(e){return '';}})()");
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.String) return null;
            var text = doc.RootElement.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex)
        {
            LogToFile($"GetSelectedTextAsync failed: {ex.Message}");
            return null;
        }
    }

    private async Task FlushPendingAsync()
    {
        if (!_webReady || _pendingMarkdown is null)
        {
            return;
        }

        var contentBaseUri = $"https://{ContentHost}/";
        Post(new
        {
            type = "render",
            markdown = _pendingMarkdown,
            sourcePath = _pendingRelativePath ?? "",
            contentBaseUri,
            theme = _pendingTheme ?? "light",
        });

        _pendingMarkdown = null;
        await Task.CompletedTask;
    }

    private void Post(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            Web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            LogToFile($"Post failed: {ex.Message}");
        }
    }

    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            switch (type)
            {
                case "log":
                    // The renderer surfaces JS errors / warnings via "log" messages.
                    var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                    if (!string.IsNullOrEmpty(text))
                    {
                        LogToFile($"[renderer] {text}");
                    }
                    break;
                case "ready":
                    _webReady = true;
                    await FlushPendingAsync();
                    break;
                case "link":
                    var href = doc.RootElement.TryGetProperty("href", out var hrefEl) ? hrefEl.GetString() : null;
                    if (string.IsNullOrEmpty(href)) return;
                    var kind = doc.RootElement.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : "relative";
                    if (kind == "external")
                    {
                        ExternalLinkClicked?.Invoke(href);
                    }
                    else if (kind == "relative")
                    {
                        RelativeMarkdownLinkClicked?.Invoke(href);
                    }
                    // anchor: handled by browser scroll, no action needed
                    break;
                case "search/result":
                    var total = doc.RootElement.TryGetProperty("total", out var tEl) ? tEl.GetInt32() : 0;
                    var current = doc.RootElement.TryGetProperty("current", out var cEl) ? cEl.GetInt32() : 0;
                    SearchResult?.Invoke(total, current);
                    break;
                case "copy":
                    var copyText = doc.RootElement.TryGetProperty("text", out var ctEl) ? ctEl.GetString() : null;
                    if (!string.IsNullOrEmpty(copyText))
                    {
                        try
                        {
                            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                            pkg.SetText(copyText);
                            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                        }
                        catch (Exception ex)
                        {
                            LogToFile($"clipboard fallback failed: {ex.Message}");
                        }
                    }
                    break;
            }
        }
        catch
        {
            // Swallow malformed messages.
        }
    }
}
