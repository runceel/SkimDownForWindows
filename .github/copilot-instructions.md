# Copilot Instructions for SkimDown for Windows

このリポジトリ固有のコーディング指針を、GitHub Copilot / 人間の参加者の両方向けにまとめる。一般的な C# / WinUI 3 のベストプラクティスは前提として記載しない。

## プロジェクト概要

SkimDown for Windows は、AI エージェントや開発ツールが生成する Markdown フォルダーを、ツリー + 静的プレビュー (WebView2) で読むだけに特化した Windows ネイティブビューアー (macOS 版 [SkimDown](https://github.com/07JP27/SkimDown) の移植)。

- **GUI フレームワーク**: WinUI 3 + Windows App SDK 2.0.1
- **言語 / ランタイム**: C# / .NET 10
- **MVVM**: CommunityToolkit.Mvvm
- **DI**: Microsoft.Extensions.DependencyInjection (`App.Services` をルートとし、ウィンドウごとに `IServiceScope`)

## アーキテクチャー (要点)

```
Presentation (App, WinUI 3)
  │   - XAML, コードビハインド, コンポジションルート
  │   - DispatcherQueueUiDispatcher, WindowService, MainWindowHandle
  │
  ├──> Infrastructure (net10.0-windows10.0.26100.0)
  │       - LocalFileSystem, FileSystemFolderWatcher,
  │         JsonSettingsRepository, WindowsClipboardService,
  │         ExplorerShellService, UiSettingsThemeProvider,
  │         LauncherExternalUriService, FileAppLogger
  │       - WinRT のみ参照。Microsoft.WindowsAppSDK は不参照
  │
  └──> Application (net10.0)
          - 抽象 I/F (IFileSystem, IFolderWatcher, ISettingsRepository,
            IClipboardService, IShellService, ISystemThemeProvider,
            IUiDispatcher, IExternalUriLauncher, IWindowService, IAppLogger,
            IMarkdownFileReader)
          - 純粋サービス (MarkdownScanner, MarkdownTreeBuilder,
            InitialSelectionPicker, LinkResolver, CommandLineLauncher)
          - ViewModel (MainPageViewModel : IDisposable)
          - 永続化 DTO (AppSettings, FolderState)
          - UI バインドモデル (MarkdownTreeItem, LoadRequest, RecentFolderEntry)
          - Utilities (PathHelpers)
          │
          └──> Domain (net10.0)
                  - 値オブジェクト・列挙のみ (AppTheme, SidebarPosition,
                    LinkKind, LinkClassification)
                  - 依存ゼロ
```

依存方向は **内向き** (Presentation → Infrastructure → Application → Domain) のみ。逆方向の参照は禁止。

詳細な設計判断の経緯は [`.github/adr/0002-clean-architecture-layered-projects.md`](.github/adr/0002-clean-architecture-layered-projects.md) を参照。

## 必ず守るルール

1. **ViewModel / Application 層から外部 I/O を直接呼ばない**
   - 禁止: `System.IO.File`, `System.IO.Directory`, `System.Diagnostics.Process`, `Windows.UI.ViewManagement.UISettings`, `Windows.ApplicationModel.DataTransfer.Clipboard`, `Windows.Storage.ApplicationData`, `Windows.System.Launcher` 等
   - すべて Application 層に定義された `I<X>` 抽象経由で呼ぶ
   - 例外: Application 内の `PathHelpers` は `Path.*` (pure な計算 API) のみ使用可

2. **`static` シングルトン (可変状態あり) を新設しない**
   - `static class X`、`public static T Instance`、`public static T Current` を可変状態を持つクラスに付けるのは禁止
   - グローバルに 1 つ必要なものは、抽象 I/F + Singleton 登録で扱う
   - 純粋な `static` ヘルパー (`PathHelpers.IsMarkdownFile` 等) は許可

3. **WebView2 へのデータ受け渡しは `PostWebMessageAsJson` のみ**
   - `NavigateToString` は使わない (SPEC: 二重 origin 分離を破壊するため)
   - 外部リンクは `IExternalUriLauncher` 経由 (`Windows.System.Launcher` 直呼び不可)

4. **新規サービスは抽象を Application、実装を Infrastructure に置く**
   - Infrastructure は `Microsoft.WindowsAppSDK` を参照しない (WinUI 依存実装は Presentation に置く)
   - 抽象は `SkimDownForWindows.Application.Abstractions`、実装は `SkimDownForWindows.Infrastructure.IO` または `SkimDownForWindows.Infrastructure.Windows`

5. **ウィンドウ寿命のサービスは Scoped、それ以外は Singleton**
   - `IFolderWatcher`, `MainPageViewModel`, Markdown 純粋サービス, `CommandLineLauncher`: **Scoped**
   - その他の I/F 実装: **Singleton**
   - `MainPageViewModel` は `IDisposable` で watcher 購読解除を保証する

6. **XAML / Page / UserControl / ResourceDictionary は App プロジェクトに閉じる**
   - クラスライブラリ (Application / Infrastructure) に XAML を置かない
   - `MarkdownPreview` (`UserControl`) は App プロジェクトに残す

7. **設計判断は ADR を書く**
   - 層境界・外部依存・横断ポリシー・セキュリティモデルを変える時は `.github/adr/` に新 ADR を追加 (運用ルールは [`.github/adr/README.md`](.github/adr/README.md))
   - Accepted な ADR の本文は書き換えず、撤回時は新 ADR + `Superseded by NNNN`

8. **現状スナップショット (`docs/`) はコードと同じ PR で更新する**
   - リポジトリルートの [`docs/`](docs/) は「いまコードがどう実装されているか」を中立に説明する技術リファレンス。ADR (歴史) / SPEC (要件) / README (使い方) / この `copilot-instructions.md` (規約) のいずれとも役割が違う
   - アーキ境界 / DI 登録 / 主要 I/F / WebView2 メッセージプロトコル / `AppSettings` schema / activation flow / Markdown パイプライン / テーマ解決ロジック を変える PR は、対応する `docs/*.md` も同じ PR で更新する
   - どの変更でどの `docs/` を更新するかの早見表は [`docs/README.md` の「更新ライフサイクル」](docs/README.md#更新ライフサイクル-ドリフト対策)。書き方の規約と PR レビューチェックリストは [`.github/skills/docs/SKILL.md`](.github/skills/docs/SKILL.md)
   - `docs/` は **現在形・中立**で書く。命令形 (「〜してください」「〜は禁止」) は書かない (それはこの規約 / SKILL の役割)

## ファイル配置のルール

| 種類 | 配置 |
|---|---|
| 列挙・純粋値オブジェクト | `SkimDownForWindows.Domain/` |
| 抽象インターフェース | `SkimDownForWindows.Application/Abstractions/` |
| 永続化 DTO / UI バインドモデル | `SkimDownForWindows.Application/Models/` |
| 純粋サービス (Markdown 解析・選択・リンク分類) | `SkimDownForWindows.Application/Markdown/` |
| ViewModel | `SkimDownForWindows.Application/ViewModels/` |
| パスユーティリティ | `SkimDownForWindows.Application/Utilities/` |
| コマンドライン解釈 | `SkimDownForWindows.Application/CommandLine/` |
| DI 登録拡張メソッド | `SkimDownForWindows.Application/DependencyInjection/`, `SkimDownForWindows.Infrastructure/DependencyInjection/` |
| ファイル I/O 実装 | `SkimDownForWindows.Infrastructure/IO/` |
| WinRT 実装 | `SkimDownForWindows.Infrastructure/Windows/` |
| コンポジションルート / WindowsAppSDK 依存実装 | `SkimDownForWindows/Composition/` |
| XAML / Page / Window / UserControl | `SkimDownForWindows/` |
| 現状スナップショット (実装構造リファレンス) | `docs/*.md` |

## 命名規約

- 抽象インターフェースは `I<Name>` (例: `IFileSystem`)
- 既定実装は具体名 (例: `LocalFileSystem`, `JsonSettingsRepository`, `ExplorerShellService`)
- DI 登録メソッドは `AddSkimDown<Layer>` (例: `AddSkimDownApplication`, `AddSkimDownInfrastructure`)
- 名前空間 = プロジェクト名 + サブフォルダー名 (`SkimDownForWindows.Application.Abstractions` 等)

## ビルド・テスト・実行

```powershell
# Debug build
dotnet build SkimDownForWindows.slnx

# 単体テスト (net10.0)
dotnet test src\SkimDownForWindows.Tests

# 上流 samples を使う統合テスト (オプトイン)
$env:SKIM_SAMPLES_PATH = "C:\path\to\SkimDown\samples"
dotnet test src\SkimDownForWindows.Tests

# WinUI 3 アプリの実行
cd src\SkimDownForWindows
dotnet build SkimDownForWindows.csproj
winapp run .\bin\<Platform>\Debug\<TargetFramework>\win-<arch> --debug-output
```

詳細は `README.md` の "Development" セクションを参照。

## よくある落とし穴

- **`Microsoft.UI.Xaml.Application` と `SkimDownForWindows.Application` の名前衝突**: App プロジェクトでは `using WinUIApplication = Microsoft.UI.Xaml.Application;` で alias する
- **`MainPage` のパラメーターレスコンストラクタ**: XAML 都合で必須。VM 解決は `OnNavigatedTo` で `MainPageStartArgs.ScopeProvider.GetRequiredService<MainPageViewModel>()`
- **ウィンドウ閉じる時の `IFolderWatcher` リーク**: `MainWindow.Closed` で `IServiceScope.Dispose()` が呼ばれ、`MainPageViewModel.Dispose()` が watcher を確実に dispose する。Scoped 登録を破ると消えるので注意
- **テストが Infrastructure を参照できない**: テストは `net10.0` ターゲット (プラットフォーム中立) のため、Infrastructure (`net10.0-windows*`) を参照しない。`IFileSystem` を必要とするテストでは `TestHelpers/RealFileSystem` を使う
- **`gh pr create` / `gh release create` が 403 / 404 で失敗する**: Copilot CLI のデフォルトトークン (`GH_TOKEN`) は `runceel/SkimDownForWindows` に対して READ-only。書き込み API は `Remove-Item Env:\GH_TOKEN` → `gh auth switch -u runceel` で keyring の所有者アカウントに切り替えてから実行。`"workflow" scope may be required` という出力は misleading なので scope 追加では解決しない。詳細は [`.github/skills/gh/SKILL.md`](skills/gh/SKILL.md)

## やってはいけないこと (Anti-patterns)

- ViewModel のメソッド内で `File.ReadAllTextAsync(...)` / `Directory.Exists(...)` を直接呼ぶ
- Application 層から `Process.Start`, `Windows.UI.ViewManagement`, `Windows.ApplicationModel.DataTransfer` を using する
- `new SettingsStore()` / `new FolderWatcher()` のような具象クラスを new する (DI 解決へ)
- `static MyService Instance => ...` の Singleton パターンを書く (DI Singleton 登録へ)
- WindowsAppSDK の API を Infrastructure 内で使う (Presentation に置く)
- クラスライブラリに `.xaml` ファイルを置く
- ADR を更新する代わりに Accepted ADR を書き換える
- アーキ境界 / DI 登録 / WebView2 メッセージプロトコル / `AppSettings` schema / activation flow / Markdown パイプライン / テーマ解決ロジック を変えるのに `docs/` を更新しない (= 現状スナップショットがコードと乖離する)
- `docs/` 本文に命令形や規約を書く (規約は `copilot-instructions.md`、チェックリストは SKILL。`docs/` は現在形・中立)

## さらに知るには

- 現状の技術スナップショット (実装構造リファレンス): [`docs/`](docs/) (アーキテクチャー / DI / WebView2 / テーマ / 設定 / アクティベーション)
- コーディング時のチェックリスト / 具体例: [`.github/skills/`](.github/skills/) (Clean Architecture / 単体テストの実装パターン)
- 設計判断の歴史: [`.github/adr/`](.github/adr/)
- 振る舞いの仕様 (要件): [`design/SPEC.md`](design/SPEC.md)
- リポジトリ概要 / ビルド手順: [`README.md`](README.md)
- 上流 macOS アプリ: <https://github.com/07JP27/SkimDown>
