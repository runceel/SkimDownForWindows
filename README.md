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

## SPEC compliance — what is and isn't covered

This port targets the [original macOS `SPEC.md`](https://github.com/07JP27/SkimDown/blob/main/design/SPEC.md).
The table below cross-references every requirement and explicitly marks
items that are deferred so there is no ambiguity about scope.

### ✅ SPEC MVP — implemented

- フォルダを開く (menu / `Ctrl+O` / empty-state button / drag-drop), `Cmd+O` mapped to `Ctrl+O`
- 起動時の前回フォルダ復元、`Open Recent`、空ウィンドウへのドロップ
- ファイル検出: `.md` / `.markdown`、再帰、`.git`/`node_modules`/`.build`/`DerivedData`/隠しファイル除外、Markdown を含まない空フォルダ除外
- ツリー: VS Code 風、フォルダ先 → ファイル、名前順 (大文字小文字非区別)、選択中ハイライト、開閉状態の保存、Markdown ファイル数表示
- 初期選択: 前回ファイル → `README.md` → 先頭ファイル → 空状態
- プレビュー: `WebView2` 描画、本文左揃え、読みやすい幅 + 余白、Light/Dark/System、`View > Zoom` でフォントサイズ
- Markdown: 見出し / 段落 / 強調・打ち消し / リスト・タスクリスト / インラインコード・コードブロック / 表 / 引用 / 水平線 / リンク / ローカル & 外部画像 / 自動リンク / 脚注 / 安全な HTML 埋め込み (DOMPurify)
- コードブロック: シンタックスハイライト (highlight.js)、長行は折り返し (`white-space: pre-wrap`)、等幅フォント、横スクロール抑止
- 表: 罫線・控えめな背景、表内のみ横スクロール
- HTML 埋め込み: `details`/`summary`/`kbd`/`mark`/`sup`/`sub`/`br`/`span`/`div` 許可、`script`/`iframe`/`object`/`embed`/`style` と `onclick` 等イベント属性 / `javascript:` 等危険スキームを除去
- リンク: ページ内アンカー、相対 Markdown はアプリ内遷移、外部は既定ブラウザ、フォルダ外ローカルは拒否
- 画像: 開いたフォルダ内ローカル画像を本文表示、外部画像も許可、画像単体はツリーに出さない
- 本文検索: `Ctrl+F` で検索バー、入力に応じてハイライト、`Enter` / `Shift+Enter` で次 / 前、`Esc` で閉じる、一致件数と現在位置表示、大文字小文字切り替え (JS 側実装)
- 変更検知: 追加 / 削除 / リネームでツリー更新、外部更新で自動再読み込み、削除で空状態
- 空状態: 中央の `Open Folder…` ボタン、フォルダのドロップ受付、Markdown が無いフォルダで `No Markdown files found` + `Open Another Folder…`
- ウィンドウタイトル: 未選択 = `SkimDown`、選択時 = `FolderName — SkimDown`
- メニュー: `File > Open Folder…/Open Recent/Close Window/Reveal in File Explorer/Copy File Path`、`Edit > Find…/Find Next/Find Previous`、`View > Toggle Sidebar/Zoom/Theme`
- 保存する状態: 前回フォルダ / 最近開いた / フォルダごとの最後の Markdown / 開閉状態 / サイドバー表示 / サイドバー幅 / テーマ / フォントサイズ / 検索の大文字小文字
- セキュリティ: フォルダピッカーで選んだフォルダのみアクセス、読み取り専用、Markdown 内の任意 JS 実行禁止、HTML サニタイズ、外部リンクは既定ブラウザ、外部 API 通信なし

### ❌ SPEC MVP に明記されているが未対応 (Deferred from SPEC's MVP)

すべての未対応項目は GitHub Issues 化されており、
[Milestone: SPEC MVP completion](https://github.com/runceel/SkimDownForWindows/milestone/1)
で進捗を追えます。各 Issue は SPEC 参照 / 受け入れ基準 / 実装ヒント付きです。

| SPEC 項目 | 状態 | Issue |
|---|---|---|
| Markdown 数式 (KaTeX) | ❌ Deferred | [#2](https://github.com/runceel/SkimDownForWindows/issues/2) |
| Mermaid 図 (`mermaid` fenced code block) | ❌ Deferred | [#3](https://github.com/runceel/SkimDownForWindows/issues/3) |
| 複数ウィンドウ (`File > New Window`, `Cmd+N`) | ❌ Deferred | [#4](https://github.com/runceel/SkimDownForWindows/issues/4) |
| サイドバー左右切り替え (`View > Move Sidebar to ...`) | ❌ Deferred | [#5](https://github.com/runceel/SkimDownForWindows/issues/5) |
| コードブロック → 右上の言語名表示 | ❌ Not implemented | [#6](https://github.com/runceel/SkimDownForWindows/issues/6) `good first issue` |
| `Edit > Find > Use Selection for Find` (`Cmd+E`) | ❌ Not implemented | [#7](https://github.com/runceel/SkimDownForWindows/issues/7) `good first issue` |
| コードブロック → 右上のコピー ボタン | ❌ Not implemented | [#8](https://github.com/runceel/SkimDownForWindows/issues/8) `good first issue` |

### ⚪ SPEC で明示的に MVP 外、または「省略してよい」とされている項目

これらは元 SPEC が "MVP 外" / "将来拡張" / "省略してよい" と明記しているため、本実装でも未対応です。

- スクロール位置のファイル毎保存 ("重くなる場合はMVPでは省略してよい")
- 手動 Reload メニュー ("MVPでは不要")
- ファイル名検索 / 複数ファイル横断検索 ("将来拡張")
- 文字コード自動判定 / Shift_JIS 対応 ("MVP外")
- Mermaid 図のズーム・パン操作 ("MVP外")
- 専用 Settings 画面 ("MVPでは作らない")
- `File > Save/Export/Print` ("MVP外")
- UI 自動テスト ("MVP外")

### 🆕 SPEC に存在しないため非対応 (オリジナルでも MVP 範囲外と推測される項目)

upstream の `samples/extended/` で言及されているが SPEC には記載がない構文。本実装でも未対応で、標準的な Markdown 表現 (通常の引用 / 文字列) として描画されます。

- GitHub Alerts (`> [!NOTE]`, `> [!TIP]`, etc.)
- 絵文字ショートコード (`:smile:`)

## License

Mirrors the upstream SkimDown project licensing intent.
See https://github.com/07JP27/SkimDown for the source SPEC, design notes,
and macOS reference implementation.
