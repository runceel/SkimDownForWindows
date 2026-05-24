# 0004. VS Code 互換のカスタムカラースキーマ対応

- 日付: 2026-05-24
- ステータス: Accepted
- 関連 ADR: [0002](0002-clean-architecture-layered-projects.md)

## コンテキスト

SkimDown for Windows はこれまで `System` / `Light` / `Dark` の 3 種の組み込みテーマのみを提供していた。
本家 macOS 版に [PR #40 "Add VS Code-style custom color schemes"](https://github.com/07JP27/SkimDown/pull/40) が登場し、
VS Code 互換 JSON テーマファイルを差し込んでプレビュー領域の配色をカスタマイズできるようになった。
Windows port でも同じユーザー体験 (テーマ JSON / メニュー UI / 削除耐性) を提供することが望まれた。

実装方針を決めるうえで、Windows port 固有の制約があった。

- Clean Architecture の依存方向 (ADR-0002) を守る必要がある。Application 層は `System.IO` を直接呼べない
- WebView2 への HTML / CSS 受け渡しは `PostWebMessageAsJson` のみ (`NavigateToString` 禁止) — 本家の「HTML build 時に `<style>` 注入」アプローチは使えない
- 既存の `--skim-*` CSS 変数体系 (本家は `--skimdown-*`) は変えたくない。テンプレートやドキュメントへの波及が大きい
- 既存 `settings.json` の `Theme` フィールドは `System.Text.Json` の既定で整数として保存されていた可能性がある (`JsonStringEnumConverter` を入れていなかった)
- `AppTheme` enum は多数の `switch` 式から参照されているため、構造ごと変える (`struct` 化等) と差分が大きい

## 決定

### 1. レイヤー配置

カスタムカラースキーマ周りの責務を以下に配置する。

| 層 | 型 |
|---|---|
| Domain | `AppTheme` (`System` / `Light` / `Dark` / **`Custom`** を追加) |
| Application/Models | `AppSettings.CustomThemeId` (新規プロパティ + `NormalizeAfterLoad()`)、`ThemeSelection(AppTheme, string?)`、`ColorScheme` + `ColorSchemeType`、`AppThemeJsonConverter` |
| Application/Abstractions | `IColorSchemeSource` (テーマ JSON 列挙の抽象 + Themes フォルダーパス) |
| Application/Theme | `ColorMapping` (純粋テーブル) / `ColorValueValidator` (純粋関数) / `ResolvedTheme` (純粋値オブジェクト) / `ColorSchemeRegistry` (Singleton) |
| Infrastructure/IO | `SettingsFolderProvider` (設定 / Themes の共通基底フォルダー解決) / `LocalColorSchemeSource` (`*.json` を実 IO で読む) |
| Presentation | `MainPage.xaml`/`.cs` の Theme サブメニュー動的構築 + `MarkdownPreview` API 拡張 + `MainWindow.ApplyTheme(theme, isDark?)` |

`ColorSchemeRegistry` は **Singleton** で登録する (テーマ JSON は app-global 状態を持つ)。
`MainPageViewModel` は Scoped のまま、コンストラクター注入で `ColorSchemeRegistry` を受け取る。

### 2. `AppTheme` 拡張方式

`AppTheme` enum を struct/record に変更する代わりに、

- `AppTheme.Custom` 値を追加し、
- `AppSettings.CustomThemeId` プロパティを並設し、
- 不整合状態 (`Theme=Custom && CustomThemeId が空 / 登録されていない`) を起動時とリロード時に `System` に正規化する (`AppSettings.NormalizeAfterLoad()` + `ColorSchemeRegistry.Normalize()`)

スイッチ式の置き換え範囲を最小化することを優先した。
`ThemeSelection(AppTheme, string?)` 値オブジェクトを `Application/Models` に置き、UI 側で「テーマと ID をセットで持ち回す」ためのヘルパーとする。

### 3. JSON 永続化の後方互換

`AppThemeJsonConverter` を導入する。

- **読み**は寛容: 整数 (`0/1/2/3`) / 大文字小文字混在の文字列 (`"System"`) / 新フォーマットの小文字 (`"system"|"light"|"dark"|"custom"`) を全部受理
- **書き**は新フォーマットの小文字に統一

`JsonSettingsRepository` で `JsonSerializerOptions.Converters` に登録、`Load()` の最後で `NormalizeAfterLoad()` を呼ぶ。これにより旧 `settings.json` (整数で `Theme: 2` が入っているもの) もそのまま動く。

### 4. CSS 変数命名

既存命名 `--skim-*` を維持する。

- `ColorMapping` は VS Code キーから `--skim-*` への優先順位付きマッピングを 12 件保持する
- `FallbackPalette` は `:root` (light) と `body[data-theme="dark"]` の値を C# 側にミラーし、欠落 VS Code key を埋める
- `skimdown.css` には `body[data-theme="custom"][data-theme-type="dark"]` セレクタを `body[data-theme="dark"]` と同じ位置に**併記**して dark 基本フォールバック (`--skim-bg` 等の全変数) を提供する。これがないと、custom dark テーマで一部 VS Code key が欠けたとき `:root` の light 値に戻ってしまう

本家との JSON 形式互換は完全に保ちつつ、CSS 変数名は port 固有とする。

### 5. WebView2 への CSS 注入

`PostWebMessageAsJson` の `render` / `theme` メッセージに `themeVars` (`--skim-*` の辞書) と `themeIsDark` (bool) を追加する。
renderer 側 (`renderer.js`):

- `--skim-` プレフィックスを持つキーのみ受理 (whitelist)
- 前回適用した変数名を `appliedCustomVars` 配列で保持し、新テーマ適用前に `removeProperty()` で剥がしてから `setProperty()`
- カスタムテーマ時は Mermaid を `theme: "base"` + `themeVariables` で初期化 (built-in は `"default"`/`"dark"`)
- `body[data-theme]` を `"light"|"dark"|"custom"`、`body[data-theme-type]` を `"light"|"dark"` に設定 (CSS セレクタ用)

C# 側 `ColorValueValidator` で hex / `rgb()` / `rgba()` / `hsl()` / `hsla()` / `transparent` のみを許可 (最大長 64) し、`url()` / `var()` / `calc()` / `;` / `{` / `}` を含む値は拒否する。これにより JSON テーマ越しの CSS injection を防ぐ。

### 6. Reload Themes / ライフサイクル

- `ColorSchemeRegistry.Reload()` で内部 snapshot を atomic に差し替え、`ThemesChanged` イベントを発火
- `MainPage` は `OnNavigatedTo` でイベントを購読、`OnUnloaded` で unsubscribe (Singleton イベント購読リーク防止)
- ハンドラ内は必ず `DispatcherQueue.TryEnqueue` で UI スレッドへ marshal
- 起動時シーケンス: **`Reload → Normalize → Save → Apply`** の順序を `MainPage.OnLoaded` で明示

`Open Themes Folder` は `IShellService.Reveal()` 経由 (`Process.Start` 直呼び禁止)。フォルダー未作成時は `IColorSchemeSource.EnsureDirectoryExists()` で作る。

### 7. Themes フォルダーの場所

`{settings_folder}/Themes/` を採用 (`SettingsFolderProvider.GetThemesFolder()`)。

- パッケージ実行時: `Windows.Storage.ApplicationData.Current.LocalFolder.Path\Themes`
- 非パッケージ実行時: `%LOCALAPPDATA%\SkimDownForWindows\Themes`

`JsonSettingsRepository.GetDefaultFolder()` のロジックを `SettingsFolderProvider` に抽出し、設定保存と Themes 読み込みで共有する。

## 結果（Consequences）

### ポジティブ

- 既存 `--skim-*` CSS / レイアウトを変更せず VS Code テーマの主要な配色だけ差し替えられる
- 旧 `settings.json` (整数 enum) を読み込めるので、既存ユーザーの設定が壊れない
- `IColorSchemeSource` 抽象により Application 層を Infrastructure に依存させずユニットテスト可能 (`InMemoryColorSchemeSource` を `TestHelpers` に追加)
- CSS Variable 適用後に Mermaid 図表色を自動追従させられる
- 不整合状態を `NormalizeAfterLoad` と `Normalize(ThemeSelection)` の 2 箇所で扱うことで、テストしやすく Defense in depth が効く
- カスタムテーマで一部 VS Code key が欠落しても、`FallbackPalette` (C# side) と `body[data-theme="custom"][data-theme-type="dark"]` 基本フォールバック (CSS side) の二段構えで dark テーマが light に化けない

### ネガティブ

- `AppTheme` のスイッチ式に `Custom` の case が増える (現状 `MainPage` / `MainWindow` 等で対応; 既定 case で `Light` 扱いにフォールバックする箇所が増えた)
- `MarkdownPreview.SetTheme` / `LoadAsync` のシグネチャに optional 引数が増えて少し複雑になった
- `JsonSettingsRepository` が `AppThemeJsonConverter` に依存するため、Infrastructure の単体テスト範囲がやや広がる (現状はテストプロジェクトが net10.0 のため Infrastructure をテストしない方針継続)

### ニュートラル

- `tokenColors` (シンタックスハイライト) は引き続き未対応 (本家 PR でもスコープ外)
- テーマ JSON の自動監視は行わない。ユーザーは `Reload Themes` メニューを手動で押す必要がある
- Windows port 専用の `--skim-*` 命名のままなので、本家との CSS 変数名は揃わない

## 検討した代替案

### 代替案 A: `AppTheme` を `struct` / `record` 化して `Custom(id: string)` を表現

- 概要: Swift の associated value enum と同じ表現を C# で再現するため、`AppTheme` を `readonly record struct AppTheme(AppThemeKind Kind, string? CustomId)` 等に置き換える。
- 採用しなかった理由: 既存の `switch (theme) { AppTheme.Dark => ... }` パターンが多数あり、それらを全部 `Kind` ベースに書き換えるか、record の `Equals(AppTheme.Dark)` を使うように直す必要がある。差分が大きく、リスクに見合わない。`AppTheme.Custom` enum + sibling `CustomThemeId` で同等の表現力が出る。

### 代替案 B: WebView2 に HTML を再生成して `<style>` ブロックを書き込む (本家方式)

- 概要: 本家 macOS 版は `MarkdownWebView.buildHTML(...)` の HTML 文字列に `<style>:root[data-theme=custom]{ ... }</style>` を毎回埋め込んで `loadHTMLString` で WebView に渡す。
- 採用しなかった理由: SkimDown for Windows のセキュリティモデルは「コンテンツ (ユーザー Markdown) と app shell (HTML) の二重 origin を分離し、Markdown 本体は `PostWebMessageAsJson` のみで渡す」というもの (ADR 検討に出てきたとおり)。HTML 文字列を再生成する経路を開けると、テーマ JSON 経由で任意の `<style>` を埋め込むことになり、二重 origin の意味が薄れる。`document.documentElement.style.setProperty()` で同等の効果が得られ、whitelisted CSS 変数名のみ受理する構造を維持できる。

### 代替案 C: CSS 変数名を本家と揃えて `--skimdown-*` にリネーム

- 概要: `skimdown.css` 全体と関連 HTML を `--skimdown-bg` 等にリネームし、本家と完全に共通の VS Code → CSS マッピングを使う。
- 採用しなかった理由: `skimdown.css` 354 行 + `renderer.js` の computed style 参照 + 既存テストへの波及が大きい。本家との互換は JSON テーマファイル形式と振る舞いで担保されており、内部 CSS 変数名を揃える実利は薄い。

## 参考リンク

- 本家 PR: <https://github.com/07JP27/SkimDown/pull/40>
- VS Code Theme Color reference: <https://code.visualstudio.com/api/references/theme-color>
- 関連 ADR: [0002 クリーンアーキテクチャー風のプロジェクト分割と DI 導入](0002-clean-architecture-layered-projects.md)
