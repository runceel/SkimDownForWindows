using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;
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
    /// <summary>
    /// Raised when the WebView2's child HWND has focus and the user presses a
    /// keyboard accelerator that WinUI's <see cref="Microsoft.UI.Xaml.Input.KeyboardAccelerator"/>
    /// would normally handle. The renderer detects the combo, calls
    /// <c>preventDefault()</c>, and posts the shortcut id back so the host can
    /// invoke the same action the menu would. See <c>renderer.js</c> for the
    /// list of ids.
    /// </summary>
    public event Action<string>? ShortcutInvoked;
    /// <summary>
    /// Raised when the user changes zoom via in-renderer gestures
    /// (Ctrl+MouseWheel or trackpad pinch). The renderer applies the new
    /// factor locally and debounces the notification so the host can persist
    /// the final value once the gesture settles.
    /// </summary>
    public event Action<double>? ZoomChanged;

    private bool _initialized;
    private bool _webReady;
    private string? _pendingMarkdown;
    private string? _pendingRelativePath;
    private string? _pendingTheme;
    private string? _pendingThemeType;
    private bool? _pendingThemeIsDark;
    private IReadOnlyDictionary<string, string>? _pendingThemeVars;
    private bool _hasPendingThemeOnlyUpdate;
    private double? _pendingZoom;
    private string? _pendingContentMaxWidth;
    private bool? _pendingTableOfContentsVisible;
    private IReadOnlyDictionary<string, string>? _pendingStrings;
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

        // Disable WebView2's built-in pinch-to-zoom on touch screens. We
        // handle zoom ourselves in renderer.js (Ctrl+Wheel + trackpad pinch
        // arrive as ctrlKey wheel events) so the host owns ZoomFactor as the
        // single source of truth. Older WebView2 runtimes may not have this
        // property — best-effort.
        try { core.Settings.IsPinchZoomEnabled = false; }
        catch { /* older WebView2 runtime lacks this knob; ignore */ }

        // Stop the browser from acting on its own accelerators (Ctrl+F find
        // bar, Ctrl+P print, Ctrl+plus/minus zoom, Ctrl+R reload, F12 dev
        // tools, etc.). The renderer's keydown listener forwards every
        // shortcut we actually care about back to the host instead, so these
        // are pure no-ops from the user's perspective once disabled.
        try { core.Settings.AreBrowserAcceleratorKeysEnabled = false; }
        catch { /* older WebView2 runtime lacks this knob; ignore */ }

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
        // 通常運用ではエラーや異常イベントのみ記録する。
        // DI が初期化済みなら IAppLogger 経由でファイルに書き、未初期化ならフォールバックで直接書き込む。
        try
        {
            var logger = App.Services?.GetService<IAppLogger>();
            if (logger is not null)
            {
                logger.LogWarning($"[MarkdownPreview] {msg}");
                return;
            }
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
    ///
    /// <paramref name="theme"/> drives the body data-theme attribute ("light" / "dark" / "custom").
    /// For custom themes pass also <paramref name="themeIsDark"/> and the resolved CSS variable
    /// dictionary in <paramref name="themeVars"/>.
    /// </summary>
    public Task LoadAsync(
        string markdown,
        string relativePath,
        string theme,
        bool? themeIsDark = null,
        IReadOnlyDictionary<string, string>? themeVars = null)
    {
        _pendingMarkdown = markdown;
        _pendingRelativePath = relativePath;
        _pendingTheme = theme;
        _pendingThemeType = ResolveThemeType(theme, themeIsDark);
        _pendingThemeIsDark = themeIsDark ?? (_pendingThemeType == "dark");
        _pendingThemeVars = CloneThemeVars(themeVars);
        _hasPendingThemeOnlyUpdate = false;
        return FlushPendingAsync();
    }

    public void ShowEmpty()
    {
        if (_webReady)
        {
            Post(new { type = "empty" });
        }
    }

    /// <summary>
    /// Push a theme change to the renderer. For built-in themes, only <paramref name="theme"/>
    /// is needed; for custom themes pass <paramref name="themeIsDark"/> and <paramref name="themeVars"/>
    /// so the renderer can switch hljs / Mermaid and inject the CSS variables.
    /// </summary>
    public void SetTheme(
        string theme,
        bool? themeIsDark = null,
        IReadOnlyDictionary<string, string>? themeVars = null)
    {
        _pendingTheme = theme;
        _pendingThemeType = ResolveThemeType(theme, themeIsDark);
        _pendingThemeIsDark = themeIsDark ?? (_pendingThemeType == "dark");
        _pendingThemeVars = CloneThemeVars(themeVars);

        if (_webReady)
        {
            Post(new
            {
                type = "theme",
                theme,
                themeType = _pendingThemeType,
                themeIsDark = _pendingThemeIsDark.Value,
                themeVars = _pendingThemeVars,
            });
            _hasPendingThemeOnlyUpdate = false;
        }
        else
        {
            // WebView2 がまだ ready でない場合は、ready 時に theme だけでも送れるよう
            // pending フラグを立てておく (markdown が無いケース対策)。
            _hasPendingThemeOnlyUpdate = true;
        }
    }

    private static string ResolveThemeType(string theme, bool? themeIsDark)
    {
        if (themeIsDark.HasValue)
        {
            return themeIsDark.Value ? "dark" : "light";
        }
        if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return "dark";
        }
        return "light";
    }

    private static IReadOnlyDictionary<string, string>? CloneThemeVars(IReadOnlyDictionary<string, string>? vars)
    {
        if (vars is null || vars.Count == 0)
        {
            return null;
        }
        var clone = new Dictionary<string, string>(vars.Count, StringComparer.Ordinal);
        foreach (var kv in vars)
        {
            if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value))
            {
                continue;
            }
            // 安全網: --skim-* プレフィックスのみ通す (renderer 側でも再チェック)。
            if (!kv.Key.StartsWith("--skim-", StringComparison.Ordinal))
            {
                continue;
            }
            clone[kv.Key] = kv.Value;
        }
        return clone.Count == 0 ? null : clone;
    }

    public void SetZoom(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        // Always remember the latest requested factor so we can resync the
        // renderer even if the caller raced ahead of `_webReady` (the typical
        // startup case: MainPage.OnLoaded restores the persisted ZoomFactor
        // before the renderer posts "ready"). Without this the renderer would
        // stay at its default 1.0 while AppSettings.ZoomFactor is e.g. 2.0,
        // and a subsequent Ctrl+wheel gesture would zoom from 1.0 and
        // overwrite the persisted setting with a wrong baseline.
        _pendingZoom = factor;
        if (_webReady)
        {
            Post(new { type = "zoom", factor });
        }
    }

    /// <summary>
    /// 本文の <c>max-width</c> CSS 値 (例: <c>"760px"</c> / <c>"none"</c>) を renderer に送り、
    /// <c>--skim-content-max</c> を inline で上書きさせる。
    /// renderer 未 ready 時は pending に保持し、<see cref="FlushPendingAsync"/> でまとめて送る。
    /// </summary>
    public void SetContentMaxWidth(string cssValue)
    {
        if (string.IsNullOrEmpty(cssValue)) return;
        _pendingContentMaxWidth = cssValue;
        if (_webReady)
        {
            Post(new { type = "contentMaxWidth", value = cssValue });
        }
    }

    public void SetTableOfContentsVisible(bool visible)
    {
        _pendingTableOfContentsVisible = visible;
        if (_webReady)
        {
            Post(new { type = "tocVisible", visible });
        }
    }

    /// <summary>
    /// Push a localized strings dictionary to the renderer (for example,
    /// "mermaidZoom.openHint" and "tableOfContents.title"). Keys are
    /// renderer-internal JS-friendly identifiers; values are display strings
    /// already resolved from <c>Resources.resw</c> by the caller.
    ///
    /// The renderer keeps English defaults so this can be called any number
    /// of times (including zero) without breaking the UI. The next call wins
    /// (merge semantics inside the renderer).
    /// </summary>
    public void SetStrings(IReadOnlyDictionary<string, string>? strings)
    {
        if (strings is null || strings.Count == 0) return;
        // Defensive copy to keep host -> renderer invariants stable across the
        // pending/flush boundary (caller cannot mutate after handing off).
        var clone = new Dictionary<string, string>(strings.Count, StringComparer.Ordinal);
        foreach (var kv in strings)
        {
            if (string.IsNullOrEmpty(kv.Key) || kv.Value is null) continue;
            clone[kv.Key] = kv.Value;
        }
        if (clone.Count == 0) return;
        _pendingStrings = clone;
        if (_webReady)
        {
            Post(new { type = "strings", strings = _pendingStrings });
            _pendingStrings = null;
        }
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
        if (!_webReady)
        {
            return;
        }

        // Zoom comes first so the initial render paints with the correct
        // scale (avoids a brief 1.0 -> persisted-factor reflow flicker).
        if (_pendingZoom is double pendingZoom)
        {
            Post(new { type = "zoom", factor = pendingZoom });
            _pendingZoom = null;
        }

        // Content max-width も最初の描画前に流しておく (幅の reflow を避ける)。
        if (_pendingContentMaxWidth is string pendingContentMax)
        {
            Post(new { type = "contentMaxWidth", value = pendingContentMax });
            _pendingContentMaxWidth = null;
        }

        if (_pendingTableOfContentsVisible is bool pendingTableOfContentsVisible)
        {
            Post(new { type = "tocVisible", visible = pendingTableOfContentsVisible });
            _pendingTableOfContentsVisible = null;
        }

        // ローカライズ文字列も render より前に届ける。これで Mermaid の
        // 「クリックで拡大」ヒント等が、初期描画時から localized で出る
        // (英語デフォルトのちらつきを防ぐ)。
        if (_pendingStrings is { } pendingStrings)
        {
            Post(new { type = "strings", strings = pendingStrings });
            _pendingStrings = null;
        }

        if (_pendingMarkdown is null)
        {
            // markdown は変わっていないが、theme だけ変えたいケース。
            // (例: WebView ready 前に SetTheme(...) が呼ばれた)
            if (_hasPendingThemeOnlyUpdate && _pendingTheme is not null)
            {
                Post(new
                {
                    type = "theme",
                    theme = _pendingTheme,
                    themeType = _pendingThemeType,
                    themeIsDark = _pendingThemeIsDark ?? false,
                    themeVars = _pendingThemeVars,
                });
                _hasPendingThemeOnlyUpdate = false;
            }
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
            themeType = _pendingThemeType ?? "light",
            themeIsDark = _pendingThemeIsDark ?? false,
            themeVars = _pendingThemeVars,
        });

        _pendingMarkdown = null;
        _hasPendingThemeOnlyUpdate = false;
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
                case "shortcut":
                    var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (!string.IsNullOrEmpty(id))
                    {
                        ShortcutInvoked?.Invoke(id);
                    }
                    break;
                case "zoomChanged":
                    if (doc.RootElement.TryGetProperty("factor", out var zfEl) &&
                        zfEl.ValueKind == JsonValueKind.Number &&
                        zfEl.TryGetDouble(out var zf) &&
                        double.IsFinite(zf) && zf > 0)
                    {
                        ZoomChanged?.Invoke(zf);
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
