# 0002. クリーンアーキテクチャー風の層分割と DI コンテナの導入

- 日付: 2026-05-24
- ステータス: Accepted
- 関連 ADR: [0001](0001-record-architecture-decisions.md)

## コンテキスト

SkimDown for Windows は当初、単一の `SkimDownForWindows` プロジェクトで MVVM (CommunityToolkit.Mvvm) ベースに作られていた。形は整ってきたが、次のような層境界の崩れがあった。

- **ViewModel が外部 I/O を直接呼んでいた**
  - `MainPageViewModel.OpenFolderAsync` 内で `Directory.Exists`
  - `MainPageViewModel.SelectAndLoadAsync` 内で `File.ReadAllTextAsync`
  - `MainPageViewModel.RevealInFileExplorer` 内で `Process.Start("explorer.exe", ...)`
  - `MainPageViewModel.CopyFilePath` 内で `Windows.ApplicationModel.DataTransfer.Clipboard.SetContent`
  - `MainPageViewModel.EffectiveTheme` 内で `Windows.UI.ViewManagement.UISettings`
- **Markdown 純粋サービスがファイルシステムを直接叩いていた**
  - `MarkdownScanner` が `Directory.EnumerateFileSystemEntries` / `File.GetAttributes`
- **設定永続化が `File.*` と WinRT を直接使用**
  - `SettingsStore` が `File.*` + `Windows.Storage.ApplicationData`
- **`static` シングルトンが多用されていた**
  - `WindowManager` (static class)
  - `App.DispatcherQueue` (static property)
  - `MainPage._sharedSettings` (static field)
- **DI コンテナ未導入**
  - `MainPage` ctor で `new SettingsStore()`, `new FolderWatcher()`, `new MainPageViewModel(...)`
  - `MainPageViewModel` ctor 内で `new MarkdownScanner()`, `new MarkdownTreeBuilder()`, `new InitialSelectionPicker()`, `new LinkResolver()`

これらの影響:

- ViewModel・純粋サービスの単体テストが書きにくい (実ファイル必須・OS 設定依存)
- 横断的な依存差し替え (例: ロガーやテーマ判定の置き換え) ができない
- WinUI 3 の `MainWindow` がリークしやすく、ウィンドウ寿命に紐づくサービス (`FolderWatcher` 等) の disposal が不確実
- 「どこに何を置くべきか」が暗黙ルールに依存しており、AI / 新規参画者向けのガイドが効きにくい

## 決定

**4 + 1 プロジェクト構成**のクリーンアーキテクチャー風レイアウトに分割し、**`Microsoft.Extensions.DependencyInjection`** を導入する。

### プロジェクト構成

| プロジェクト | TFM | 役割 | 主な参照 |
|---|---|---|---|
| `SkimDownForWindows.Domain` | `net10.0` | 副作用ゼロの値オブジェクト・列挙のみ (`AppTheme`, `SidebarPosition`, `LinkKind`, `LinkClassification`) | (なし) |
| `SkimDownForWindows.Application` | `net10.0` | ユースケース層: 抽象 I/F・純粋ロジック・ViewModel・UI バインドモデル | Domain + CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection.Abstractions |
| `SkimDownForWindows.Infrastructure` | `net10.0-windows10.0.26100.0` | プラットフォーム実装 (WinRT + `System.IO` ラッパー) | Application + Microsoft.Extensions.DependencyInjection.Abstractions (※ Microsoft.WindowsAppSDK は **不参照**) |
| `SkimDownForWindows` (App) | `net10.0-windows10.0.26100.0` | XAML / コードビハインド / コンポジションルート / WindowsAppSDK 依存実装 | Application + Infrastructure + Microsoft.WindowsAppSDK + Microsoft.Extensions.DependencyInjection |
| `SkimDownForWindows.Tests` | `net10.0` | Application + Domain の単体テスト | Domain + Application |

### 依存方向

```
Presentation (App)
  ├──> Infrastructure ─────┐
  └──> Application ────────┴──> Domain
```

逆方向の参照は禁止。Application は Infrastructure を**直接知らず**、I/F 経由で操作する。

### 主な抽象 (Application 層)

| 抽象 | 役割 | Infrastructure 実装 |
|---|---|---|
| `IFileSystem` | `System.IO` の最小ラッパー | `LocalFileSystem` (Infrastructure) |
| `IMarkdownFileReader` | UTF-8 で Markdown 本文を読む | `LocalMarkdownFileReader` (Infrastructure) |
| `IFolderWatcher` | `FileSystemWatcher` のラッパー | `FileSystemFolderWatcher` (Infrastructure) |
| `ISettingsRepository` | `AppSettings` の永続化 | `JsonSettingsRepository` (Infrastructure) |
| `IClipboardService` | OS クリップボード | `WindowsClipboardService` (Infrastructure) |
| `IShellService` | エクスプローラーで Reveal | `ExplorerShellService` (Infrastructure) |
| `ISystemThemeProvider` | OS のテーマ判定 | `UiSettingsThemeProvider` (Infrastructure) |
| `IExternalUriLauncher` | 既定ブラウザ起動 | `LauncherExternalUriService` (Infrastructure) |
| `IAppLogger` | ファイルロガー | `FileAppLogger` (Infrastructure) |
| `IUiDispatcher` | UI スレッドマーシャル | `DispatcherQueueUiDispatcher` (Presentation: WindowsAppSDK 依存のため) |
| `IWindowService` | ウィンドウレジストリ (旧 `static WindowManager`) | `WindowService` (Presentation) |

### DI コンテナとスコープ

- **`Microsoft.Extensions.DependencyInjection`** を採用
- ルート Provider は **`App.Services`** プロパティで公開 (`Ioc.Default` は使わない: テスト・再初期化耐性、global static の最小化)
- **ウィンドウごとに `IServiceScope`** を作成
  - `MainWindow` コンストラクタで `App.Services.CreateScope()`
  - `MainPageStartArgs` に `IServiceProvider ScopeProvider` を含めて `Frame.Navigate` で `MainPage` に渡す
  - `MainPage.OnNavigatedTo` で `scopeProvider.GetRequiredService<MainPageViewModel>()`
  - `MainWindow.Closed` で `_scope.Dispose()` → スコープ内のサービスが全 dispose される
- **`MainPageViewModel : IDisposable`** を実装し watcher 購読解除と `IFolderWatcher.Dispose` を保証

### ライフサイクル

| サービス | ライフサイクル | 備考 |
|---|---|---|
| `ISettingsRepository`, `IWindowService`, `IUiDispatcher`, `ISystemThemeProvider`, `IClipboardService`, `IShellService`, `IExternalUriLauncher`, `IFileSystem`, `IAppLogger`, `IMarkdownFileReader` | Singleton (root) | プロセス全体で 1 つ |
| `IFolderWatcher`, `MainPageViewModel`, `MarkdownScanner`, `MarkdownTreeBuilder`, `InitialSelectionPicker`, `LinkResolver`, `CommandLineLauncher` | Scoped (ウィンドウスコープ) | ウィンドウ閉鎖時に必ず dispose |
| `MainPage` | DI 非管理 | XAML パラメーターレスコンストラクタが必須なので、ナビゲーション時にスコープから VM を取得する |

### 設計原則

1. **依存方向は内向き**: Presentation → Infrastructure → Application → Domain
2. **ViewModel・Application 層から外部 I/O を直接呼ばない**: `System.IO`・`System.Diagnostics.Process`・`Windows.*` などは抽象経由のみ
3. **静的グローバル状態を避ける**: 可変状態を持つ `static` クラス・プロパティは原則禁止
4. **`new` を抑制**: ビジネスロジック内で具象クラスを `new` しない (POCO/値オブジェクトは除く)
5. **XAML は App プロジェクトに閉じる**: クラスライブラリ側に Page / UserControl / ResourceDictionary を出さない

## 結果（Consequences）

### ポジティブ

- ViewModel と純粋サービスを単体テストで完全に検証可能 (Application プロジェクトは依存ゼロの `net10.0`)
- 外部依存の置き換えがインターフェース 1 個の差し替えで完結する
- 層境界が csproj レベルで強制されるため、Application から `System.IO` を呼ぶコードはコンパイル時に検出される (using 不能)
- `MainWindow.Closed` で `IServiceScope.Dispose()` が呼ばれることで、`IFolderWatcher` リークやイベント購読リークが構造的に防止される
- ADR + copilot-instructions により設計意図がリポジトリ内で検索可能になる

### ネガティブ

- プロジェクト数が 2 → 5 に増加し、最初の認知コストが上がる
- `MarkdownPreview` や `MainWindow` などの presentation コードはコンストラクタが空でないため、デザイナー / XAML Hot Reload で表示崩れが起きうる
- `MainPage` の VM は `OnNavigatedTo` まで `null!` 状態なので、ctor 内では XAML bindings 越しに ViewModel を触れない (Loaded 以降での初期化に統一)
- `Microsoft.Extensions.DependencyInjection` 依存が増える (Application/Infrastructure は Abstractions のみ、App プロジェクトはフル MEDI)
- Release publish (`PublishTrimmed=true`) でトリム警告が出る可能性は残る (コンストラクタインジェクションのみに留めれば実質ゼロ。`verify` フェーズで検証する)

### ニュートラル

- 既存 `SettingsStore` 内の `SemaphoreSlim` + atomic tmp + move パターンはそのまま `JsonSettingsRepository` に移植 (同時書き込み制御は維持)
- 既存 `FolderWatcher` の debounce / overflow フォールバックはそのまま `FileSystemFolderWatcher` に移植
- 既存 `static WindowManager` の API は機能等価で `IWindowService` に移行。`WindowsChanged` イベント購読・購読解除のタイミングは `MainPage.Loaded` / `Unloaded` で従来通り
- テストプロジェクトは `<Compile Include>` 部分参照から `<ProjectReference>` に切り替え。`net10.0` ターゲットは維持 (Windows TFM へは昇格しない)
- Tests は Infrastructure を参照しないので、`IFileSystem` を必要とするテストでは `TestHelpers/RealFileSystem` を提供する

## 検討した代替案

### 代替案 A: 単一プロジェクトのまま、フォルダー分けで層分割

- 概要: プロジェクト数を増やさず、`Abstractions/`, `Services/`, `Platform/` フォルダーで層を表現
- 採用しなかった理由: フォルダー命名規約のみで層を強制するため、AI や新規参画者が直接 `System.IO` を呼ぶコードを書いてしまっても、コンパイル時に警告できない。利点 (シンプル) より、層強制の弱さの問題が大きい

### 代替案 B: Domain / Application / Infrastructure / Presentation の 4 層をすべて別 csproj に分ける本家クリーンアーキテクチャー

- 概要: 採用案そのもの。ただし `Microsoft.WindowsAppSDK` を Infrastructure に持たせて、Presentation はほぼ XAML のみのプロジェクトにする変種
- 採用しなかった理由: WindowsAppSDK は WinUI XAML / DispatcherQueue 等の表示寄り機能を含むため、Infrastructure に持たせると "プラットフォーム I/O" と "プレゼンテーション基盤" が混在する。本案では WindowsAppSDK 依存実装 (`DispatcherQueueUiDispatcher`, `WindowService`) のみ App プロジェクトに残し、Infrastructure は WinRT + `System.IO` のみとする

### 代替案 C: Application も Windows TFM (`net10.0-windows10.0.26100.0`)

- 概要: Application から `Windows.Storage.ApplicationData.Current` 等の WinRT 型を直接使う
- 採用しなかった理由: テストプロジェクトを `net10.0` (プラットフォーム中立) で維持できるメリットを失う。`AppSettings` などのモデルは pure であるべきなので、Application は `net10.0` に留める

### 代替案 D: 既存 `Ioc.Default` + `CommunityToolkit.Mvvm.DependencyInjection`

- 概要: CommunityToolkit.Mvvm 同梱の `Ioc.Default.ConfigureServices()` を使う
- 採用しなかった理由: `Ioc.Default` は global static であり、テスト間汚染や複数 Provider への切り替えが困難。`App.Services` プロパティ + Window-scope の組み合わせの方が柔軟性が高く、設計原則 3 (「静的グローバル状態を避ける」) に合致する

## 参考リンク

- ADR-0001 (本リポジトリの ADR 運用ルール): [0001-record-architecture-decisions.md](0001-record-architecture-decisions.md)
- Microsoft.Extensions.DependencyInjection: <https://learn.microsoft.com/dotnet/core/extensions/dependency-injection>
- CommunityToolkit.Mvvm: <https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/>
- WinUI 3 / Windows App SDK: <https://learn.microsoft.com/windows/apps/windows-app-sdk/>
