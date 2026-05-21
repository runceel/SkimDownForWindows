# SkimDown for Windows

A Windows port of [SkimDown](https://github.com/07JP27/SkimDown) — a quiet,
read-only Markdown folder reader. Open a folder, browse the Markdown tree in
the sidebar, read the selected file in a clean preview. No editor chrome, no
accidental edits.

> **Original macOS app:** Swift 6 + AppKit + `WKWebView`, by
> [@07JP27](https://github.com/07JP27).
> **This port:** WinUI 3 + Windows App SDK + WebView2 + CommunityToolkit.Mvvm,
> on .NET 10.

## Highlights

- Open a folder via **File → Open Folder…**, **Ctrl+O**, the empty-state
  button, or by dragging a folder onto the window
- Folder-first, case-insensitive Markdown tree (recursive `.md` / `.markdown`
  discovery, VS Code Explorer-style ordering)
- Hides `.git`, `node_modules`, `.build`, `DerivedData`, hidden files /
  folders, and folders that contain no Markdown
- Read-only WebView2 preview with bundled rendering assets (no CDN: works
  fully offline)
- markdown-it + highlight.js + DOMPurify ship inside the app
- In-document search (**Ctrl+F**) with next / previous navigation, case
  sensitivity toggle, and result count
- Live reload — `FileSystemWatcher` refreshes the tree and re-renders the
  current file when content changes
- Persists recent folders, last folder, per-folder last file, per-folder
  expansion state, theme, zoom factor, and sidebar width / visibility
- Light / Dark / System theme (auto-detects Windows system theme)
- Local-only — no telemetry, no network requests for rendering

## Security boundaries

Following the original SPEC, plus extra hardening for the Windows port:

- The WebView2 control uses **two distinct virtual host names**:
  `https://skimdown-app.example/` serves the bundled HTML/CSS/JS, while
  `https://skimdown-content.example/` serves only the opened folder. The two
  origins are separated so a renderer bug can't read the user's folder from
  the asset origin.
- Markdown content is delivered to the renderer via
  `CoreWebView2.PostWebMessageAsJson`. We do **not** use `NavigateToString`.
- All relative links are classified by `LinkResolver` and refused if they
  resolve outside the opened folder. Relative paths are canonicalized to
  catch `..`, URL-encoded escapes, and case games.
- The renderer pipes Markdown through `DOMPurify`; `<script>`, `<iframe>`,
  `<object>`, `<embed>`, `<style>`, and event-handler attributes are stripped
  before insertion.
- External `http(s)` links open in the user's default browser; everything
  else (`javascript:`, `mailto:`, custom schemes, `file://` outside the
  folder) is blocked.

## Project layout

```
SkimDownForWindows/
├── SkimDownForWindows.slnx        Solution file (XML format) — references both projects
└── src/
    ├── SkimDownForWindows/        Main WinUI 3 app
    │   ├── SkimDownForWindows.csproj
    │   ├── App.xaml(.cs)
    │   ├── MainWindow.xaml(.cs)
    │   ├── MainPage.xaml(.cs)
    │   ├── Package.appxmanifest
    │   ├── Core/
    │   │   ├── SettingsStore.cs       JSON settings persistence
    │   │   └── FolderWatcher.cs       FileSystemWatcher with debounce + UI marshal
    │   ├── Markdown/
    │   │   ├── MarkdownScanner.cs     Recursive .md scan with SPEC exclusions
    │   │   ├── MarkdownTreeBuilder.cs Folder-first tree, case-insensitive
    │   │   ├── LinkResolver.cs        Anchor / relative-md / external / blocked
    │   │   └── InitialSelectionPicker.cs  Last-opened → README.md → first-file
    │   ├── Models/                    AppSettings, MarkdownTreeItem, LinkClass
    │   ├── Utilities/PathHelpers.cs   Canonicalization + folder-boundary checks
    │   ├── ViewModels/MainPageViewModel.cs
    │   ├── Viewer/MarkdownPreview.xaml(.cs)   WebView2 wrapper
    │   └── Assets/Web/                Bundled renderer.html / renderer.js / CSS / vendor
    └── SkimDownForWindows.Tests/  MSTest unit tests (net10.0, no UI deps)
```

## Prerequisites

- Windows 10 1903+ (Windows 11 recommended)
- Developer Mode enabled (`Settings → System → For developers → On`)
- .NET 10 SDK
- WinUI 3 templates (`dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`)
- `winapp` CLI (`winget install Microsoft.WinAppCLI`)
- (Optional, for one-step build+run) the `winui-dev-workflow` skill from the
  [`awesome-copilot/winui`](https://github.com/microsoft/win-dev-skills)
  plugin: `copilot plugin install winui@awesome-copilot`

## Build & run

```powershell
# Build everything via the solution file:
dotnet build SkimDownForWindows.slnx

# Build + launch the WinUI 3 app (requires winapp CLI):
cd src\SkimDownForWindows
dotnet build SkimDownForWindows.csproj
winapp run .\bin\<Platform>\Debug\<TargetFramework>\win-<arch>  --debug-output

# Or, if the winui plugin is installed, one-shot build+launch with auto
# platform / packaging detection:
cd src\SkimDownForWindows
~\.copilot\installed-plugins\awesome-copilot\winui\skills\winui-dev-workflow\BuildAndRun.ps1
```

## Tests

```powershell
# 30 always-on unit tests:
cd src\SkimDownForWindows.Tests
dotnet test

# Add 9 integration tests against the upstream samples corpus
# (point SKIM_SAMPLES_PATH at a cloned copy of github.com/07JP27/SkimDown/samples):
$env:SKIM_SAMPLES_PATH = "C:\path\to\SkimDown\samples"
dotnet test          # -> 39 / 39 passing
```

The test project targets plain `net10.0` and re-includes the UI-agnostic
source files from the main project. Coverage:

| Suite | Covers |
|---|---|
| `PathHelpersTests` | Markdown extension check, folder-boundary checks, `..` escape, prefix-string trap |
| `MarkdownScannerTests` | `.md` / `.markdown` discovery, excluded directories, hidden files |
| `MarkdownTreeBuilderTests` | Folder-first order, alpha sort, omits empty folders, counts, forward-slash relative paths, out-of-root rejection |
| `InitialSelectionPickerTests` | Last-opened > README > first-file fallbacks |
| `LinkResolverTests` | Anchor / relative-md / relative-non-md / external / out-of-folder / `javascript:` / URL-encoded |
| `UpstreamSamplesIntegrationTests` | Scans + builds the actual upstream `samples/` corpus (38 files across `en/ja` × `basics/blocks/deep/extended/misc`), asserts `.markdown` extension support, deep recursion, omission of non-Markdown `images/` folder, alphabetized branches, and the picker's README fallback |

UI automation is intentionally out of scope (matches the original SPEC).

### Verified against the upstream samples

The Windows port has been smoke-tested against every file in the original
SkimDown `samples/` directory. The 15 English samples were navigated via
Windows UI Automation and screenshot-verified. Behavior summary:

| Sample | Result |
|---|---|
| `basics/headings.md` | ✅ h1–h6 hierarchy + underlines |
| `basics/text-formatting.md` | ✅ bold / italic / strikethrough / inline code |
| `basics/links-and-images.md` | ✅ links + relative images via content virtual host |
| `basics/lists.md` | ✅ ordered / unordered / task lists |
| `blocks/blockquotes.md` | ✅ blockquote styling |
| `blocks/code-blocks.md` | ✅ multi-language syntax highlighting via highlight.js |
| `blocks/tables.md` | ✅ GFM tables with column alignment + emoji + inline formatting |
| `blocks/horizontal-rules.md` | ✅ `<hr>` rendering |
| `extended/footnotes.md` | ✅ `markdown-it-footnote` references and back-refs |
| `extended/github-alerts.md` | ⚠️ Renders as plain blockquote (GitHub alert plugin not bundled in MVP) |
| `extended/emoji.md` | ⚠️ Shortcodes shown as text (emoji plugin not bundled in MVP) |
| `extended/html-elements.md` | ✅ `<kbd>`, `<details>/<summary>`, `<mark>`, sanitized via DOMPurify |
| `extended/math.md` | ⚠️ LaTeX shown as text (KaTeX deferred — graceful fallback per SPEC) |
| `extended/mermaid.md` | ⚠️ Diagrams shown as syntax-highlighted code blocks (Mermaid deferred — graceful fallback per SPEC) |
| `misc/all-in-one.md` | ✅ Combined-syntax stress test |
| `misc/sample.markdown` | ✅ Full `.markdown` extension recognised + rendered |

The ⚠️ cases all match documented MVP deferrals; the renderer falls back
to plain text or unstyled code blocks as the original SPEC prescribes.

## Manual smoke check

1. Launch the app — empty state shows the **Open Folder…** button.
2. Drag any folder containing Markdown onto the window or click **Open Folder…**.
3. The sidebar lists `.md` / `.markdown` files, folder-first, alphabetical.
4. Selecting a file renders it in the preview pane.
5. Edit one of the open files externally — the preview reloads.
6. Add / remove / rename a Markdown file — the tree refreshes.
7. `Ctrl+F` opens the search bar; type to highlight, `Enter` / `Shift+Enter`
   navigate, `Esc` closes.
8. **View → Theme → Light / Dark / System** switches preview + chrome.
9. **View → Zoom → In / Out / Actual Size** (or `Ctrl+Plus` / `Ctrl+Minus` /
   `Ctrl+0`) scales the preview.
10. **View → Toggle Sidebar** (`Ctrl+B`) hides / shows the tree.
11. Click a relative `[link](./other.md)` — selection moves to that file.
12. Click an external `https://...` link — the default browser opens it.
13. Restart the app — it reopens the last folder and selects the last file.

## What's deferred from the SPEC's MVP

- **Multiple windows** (`Cmd+N`). Single window in this initial port.
- **Sidebar left / right swap.** Sidebar is on the left.
- **Scroll-position persistence per file.**
- **KaTeX / Mermaid rendering.** The renderer is wired with markdown-it,
  highlight.js, and DOMPurify; math/diagrams are easy follow-ups.

## License

Mirrors the upstream SkimDown project licensing intent.
See https://github.com/07JP27/SkimDown for the source SPEC, design notes,
and macOS reference implementation.
