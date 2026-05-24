---
name: clean-architecture-skimdown
description: SkimDown for Windows でクリーンアーキテクチャー風のレイアウト・DI・依存方向に従って新規サービス / I/F / ViewModel を追加または既存コードをリファクタする時に使う。トリガー語の例 - "新しい I/F を追加", "サービスを足す", "ViewModel を作る", "DI 登録", "層境界", "Application から System.IO を呼びたい", "Infrastructure 実装", "Singleton か Scoped か"。設計判断の根拠は ADR-0002 を参照。
---

# Clean Architecture for SkimDown for Windows

## どんな時に使うか

- Application 層に新しい抽象 (`I<X>`) を追加する時
- Infrastructure 層に新しい実装 (`LocalXxx` / `WindowsXxx`) を追加する時
- ViewModel から外部 I/O を呼ぶ必要が出てきた時
- `static` のシングルトン / グローバルキャッシュを書きたくなった時
- PR レビューで「これは Application に置くべきか Presentation か」を判定する時

判断の正当性 (なぜこの構造になっているか) は **[ADR-0002](../../adr/0002-clean-architecture-layered-projects.md)** を参照。skill はそこで決まったことを実装時に**思い出すための圧縮表**。

## レイヤーマップ

```
Presentation (SkimDownForWindows, net10.0-windows10.0.26100.0)
  - XAML / Page / Window / UserControl
  - コンポジションルート (App.xaml.cs, MainWindow.xaml.cs)
  - WindowsAppSDK 依存実装 (DispatcherQueueUiDispatcher, WindowService)
  │
  ├──> Infrastructure (SkimDownForWindows.Infrastructure, net10.0-windows10.0.26100.0)
  │     - LocalFileSystem, LocalMarkdownFileReader, FileSystemFolderWatcher,
  │       JsonSettingsRepository, FileAppLogger (IO/ 配下)
  │     - WindowsClipboardService, ExplorerShellService,
  │       UiSettingsThemeProvider, LauncherExternalUriService (Windows/ 配下)
  │     - WinRT + System.IO のみ参照。Microsoft.WindowsAppSDK は不参照
  │
  └──> Application (SkimDownForWindows.Application, net10.0)
        - Abstractions/ : I<X> 抽象 (11 個)
        - Markdown/     : MarkdownScanner, MarkdownTreeBuilder,
                          InitialSelectionPicker, LinkResolver (純粋サービス)
        - ViewModels/   : MainPageViewModel : IDisposable
        - Models/       : AppSettings, FolderState, MarkdownTreeItem,
                          LoadRequest, RecentFolderEntry
        - CommandLine/  : CommandLineLauncher
        - Utilities/    : PathHelpers (純粋 static)
        - DependencyInjection/ : AddSkimDownApplication 拡張メソッド
        │
        └──> Domain (SkimDownForWindows.Domain, net10.0)
              - AppTheme, SidebarPosition, LinkKind, LinkClassification
              - 副作用ゼロ。依存ゼロ。
```

## 依存方向は内向きのみ

```
Presentation ──> Infrastructure ──> Application ──> Domain
                                            ↑
                                            └──── 逆方向参照は禁止
```

- Application は **Infrastructure を知らない**。`Microsoft.Extensions.DependencyInjection.Abstractions` 経由でしか具象を知らない
- Infrastructure は Application の抽象を実装するためだけに Application を参照する
- Domain は何にも依存しない

## 必ず守るルール (圧縮版)

> 詳細は [copilot-instructions.md の「必ず守るルール」](../../copilot-instructions.md) を参照。本 skill ではコーディング時に必要な要点のみ。

1. **Application / ViewModel から外部 I/O 型を `using` しない**
   - 禁止: `System.IO.File`, `System.IO.Directory`, `System.Diagnostics.Process`, `Windows.UI.ViewManagement.*`, `Windows.ApplicationModel.DataTransfer.*`, `Windows.Storage.*`, `Windows.System.Launcher`
   - 必ず `I<X>` 抽象経由で呼ぶ
   - 例外: `Application/Utilities/PathHelpers` は `System.IO.Path` の**純粋計算 API のみ**使用可
2. **可変状態を持つ `static` シングルトンを書かない**
   - `static MyService Instance => ...` / `static class WindowManager` 禁止
   - DI Singleton 登録で置き換える
   - 純粋ヘルパー (`static class PathHelpers` 内の `IsMarkdownFile` 等) は可
3. **WebView2 への入力は `PostWebMessageAsJson` のみ**
   - `NavigateToString` 禁止 (SPEC: 二重 origin 分離の維持)
   - 外部リンクは `IExternalUriLauncher` 経由
4. **新規サービスは抽象を Application、実装を Infrastructure に置く**
   - Infrastructure は `Microsoft.WindowsAppSDK` を参照しない
   - WindowsAppSDK に依存する実装 (Dispatcher / Window 系) は **Presentation の `Composition/` 配下**に置く
5. **XAML / Page / UserControl / ResourceDictionary は App プロジェクトに閉じる**
   - クラスライブラリに `.xaml` を置かない

## 新規 I/F を追加する 4 ステップ

ファイル I/O 系の例 (たとえば「ファイルに書き込みたい」)。

### Step 1: Application に抽象を追加

```csharp
// src/SkimDownForWindows.Application/Abstractions/IXxxWriter.cs
namespace SkimDownForWindows.Application.Abstractions;

/// <summary>...用途を 1-2 行で書く...</summary>
public interface IXxxWriter
{
    Task WriteAsync(string path, string content, CancellationToken ct = default);
}
```

- 名前空間 = `SkimDownForWindows.Application.Abstractions`
- ファイル名 = I/F 名と一致 (`IXxxWriter.cs`)
- メンバーには XML doc コメント (用途・例外動作・しきい値)
- 必要なら `IDisposable` を継承 (リソース保持型)

### Step 2: Infrastructure に実装を追加

```csharp
// src/SkimDownForWindows.Infrastructure/IO/LocalXxxWriter.cs
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Infrastructure.IO;

internal sealed class LocalXxxWriter : IXxxWriter { ... }
```

- IO 系は `Infrastructure/IO/`、WinRT 系は `Infrastructure/Windows/`
- 名前空間 = `SkimDownForWindows.Infrastructure.IO` または `.Windows`
- クラスは `internal sealed`、DI 経由でのみ生成される

### Step 3: DI 登録

```csharp
// src/SkimDownForWindows.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs
public static IServiceCollection AddSkimDownInfrastructure(this IServiceCollection services)
{
    ...
    services.AddSingleton<IXxxWriter, LocalXxxWriter>();  // または AddScoped
    return services;
}
```

- 抽象 → 実装で登録
- ライフサイクル選択は次節を参照

### Step 4: 利用側 (ViewModel など) で DI 解決

- コンストラクタインジェクションで受け取る (`new` しない)
- フィールドは `private readonly`

```csharp
public MainPageViewModel(..., IXxxWriter xxxWriter, ...)
{
    _xxxWriter = xxxWriter;
}
```

## DI ライフサイクル早見表

| サービス種別 | ライフサイクル | 例 |
|---|---|---|
| 状態を持たない / プロセス全体で 1 つで OK | **Singleton** | `IFileSystem`, `IMarkdownFileReader`, `ISettingsRepository`, `IClipboardService`, `IShellService`, `ISystemThemeProvider`, `IExternalUriLauncher`, `IAppLogger` |
| ウィンドウ寿命に紐づく / `IDisposable` で resource を持つ | **Scoped** | `IFolderWatcher`, `MainPageViewModel`, `MarkdownScanner`, `MarkdownTreeBuilder`, `InitialSelectionPicker`, `LinkResolver`, `CommandLineLauncher` |
| WindowsAppSDK 依存 (Dispatcher / Window) | Presentation 側で **Singleton** | `IUiDispatcher`, `IWindowService` |

> Scoped を選ぶときは `MainWindow.Closed` で `IServiceScope.Dispose()` が呼ばれることを前提にしている。Scoped を破ると watcher のイベント購読がリークするので注意。

## ファイル配置の早見表

| 種類 | 配置 | 例 |
|---|---|---|
| 値オブジェクト・enum (依存ゼロ) | `SkimDownForWindows.Domain/` | `AppTheme.cs`, `LinkClassification.cs` |
| 抽象 I/F | `SkimDownForWindows.Application/Abstractions/` | `IFileSystem.cs`, `IFolderWatcher.cs` |
| 永続化 DTO / UI バインドモデル | `SkimDownForWindows.Application/Models/` | `AppSettings.cs`, `MarkdownTreeItem.cs` |
| 純粋サービス (副作用ゼロ or `IFileSystem` のみ) | `SkimDownForWindows.Application/Markdown/` | `MarkdownScanner.cs`, `LinkResolver.cs` |
| ViewModel | `SkimDownForWindows.Application/ViewModels/` | `MainPageViewModel.cs` |
| パスユーティリティ (`static class`) | `SkimDownForWindows.Application/Utilities/` | `PathHelpers.cs` |
| コマンドライン引数の解釈 | `SkimDownForWindows.Application/CommandLine/` | `CommandLineLauncher.cs` |
| DI 拡張メソッド (Application) | `SkimDownForWindows.Application/DependencyInjection/` | `ApplicationServiceCollectionExtensions.cs` |
| DI 拡張メソッド (Infrastructure) | `SkimDownForWindows.Infrastructure/DependencyInjection/` | `InfrastructureServiceCollectionExtensions.cs` |
| ファイル I/O 実装 | `SkimDownForWindows.Infrastructure/IO/` | `LocalFileSystem.cs`, `JsonSettingsRepository.cs` |
| WinRT 実装 | `SkimDownForWindows.Infrastructure/Windows/` | `WindowsClipboardService.cs`, `ExplorerShellService.cs` |
| コンポジションルート / WindowsAppSDK 依存実装 | `SkimDownForWindows/Composition/` | `DispatcherQueueUiDispatcher.cs`, `WindowService.cs` |
| XAML / Page / Window / UserControl | `SkimDownForWindows/` | `MainWindow.xaml`, `MainPage.xaml`, `MarkdownPreview.xaml` |

## 命名規約

- 抽象は `I<Name>` (例: `IFileSystem`)
- 既定実装は具体名 (例: `LocalFileSystem`, `JsonSettingsRepository`)
- DI 拡張は `AddSkimDown<Layer>` (例: `AddSkimDownApplication`, `AddSkimDownInfrastructure`)
- 名前空間 = プロジェクト名 + サブフォルダー名

## やってはいけないこと (Anti-patterns)

### ❌ ViewModel から `System.IO` を直呼び

```csharp
// ❌ NG
public async Task SelectAndLoadAsync(string path)
{
    var text = await File.ReadAllTextAsync(path);  // Application 層から直呼び
    ...
}
```

```csharp
// ✅ OK - IMarkdownFileReader 経由
public async Task SelectAndLoadAsync(string path)
{
    var text = await _reader.ReadAsync(path);
    ...
}
```

### ❌ Application 層から `Windows.*` を using

```csharp
// ❌ NG - Application プロジェクトに Windows.* を持ち込む
using Windows.ApplicationModel.DataTransfer;
...
Clipboard.SetContent(new DataPackage());
```

```csharp
// ✅ OK - IClipboardService 経由
_clipboard.SetText(path);
```

### ❌ 具象クラスを `new`

```csharp
// ❌ NG
public MainPageViewModel()
{
    _scanner = new MarkdownScanner();  // DI で解決すべき
    _settings = new JsonSettingsRepository();
}
```

```csharp
// ✅ OK - コンストラクタインジェクション
public MainPageViewModel(MarkdownScanner scanner, ISettingsRepository settings, ...)
{
    _scanner = scanner;
    _settings = settings;
}
```

> 純粋な値オブジェクト (`new AppSettings()`, `new MarkdownTreeItem(...)`) は OK。

### ❌ `static` シングルトン

```csharp
// ❌ NG
public static class WindowManager
{
    public static List<Window> Windows { get; } = new();
}
```

```csharp
// ✅ OK - IWindowService を Singleton 登録
services.AddSingleton<IWindowService, WindowService>();
```

### ❌ Infrastructure で WindowsAppSDK を using

```csharp
// ❌ NG - Infrastructure プロジェクトで
using Microsoft.UI.Dispatching;  // WindowsAppSDK は Infrastructure では参照しない
```

```csharp
// ✅ OK - Presentation 側 (Composition/) に置く
// src/SkimDownForWindows/Composition/DispatcherQueueUiDispatcher.cs
```

### ❌ クラスライブラリに `.xaml`

```csharp
// ❌ NG - SkimDownForWindows.Application に
// MarkdownPreview.xaml を置く
```

```csharp
// ✅ OK - App プロジェクトに置く
// src/SkimDownForWindows/MarkdownPreview.xaml
```

### ❌ ADR 本文書き換え

- Accepted な ADR の本文は書き換えない (typo / リンク修正以外)
- 設計を変える時は新規 ADR を作り、旧 ADR の `ステータス` を `Superseded by NNNN` に書き換える

## チェックリスト (PR 時セルフレビュー)

- [ ] 追加した型は適切なプロジェクト / フォルダーに配置されているか
- [ ] 名前空間 = プロジェクト名 + サブフォルダー名 になっているか
- [ ] Application 層に `System.IO` / `System.Diagnostics.Process` / `Windows.*` の using が無いか
- [ ] `new` で具象クラスを生成していないか (値オブジェクトを除く)
- [ ] DI 登録のライフサイクル (Singleton / Scoped) は妥当か
- [ ] `IDisposable` を必要とするサービスは Scoped で登録されているか
- [ ] WindowsAppSDK 依存型を Infrastructure に持ち込んでいないか
- [ ] クラスライブラリに `.xaml` を置いていないか
- [ ] 既存の設計判断を変える必要があれば、新 ADR を起こしているか

## 設計判断を変えたい時

層境界・外部依存・横断ポリシー・セキュリティモデルを変える時は、コード変更前に **[ADR を新規作成](../../adr/README.md)** する。

1. `.github/adr/template.md` をコピーして `NNNN-kebab.md` を作成
2. コンテキスト / 決定 / 結果 / 検討した代替案を書く
3. レビューで合意後、`ステータス` を `Accepted` にしてマージ
4. 関連する `copilot-instructions.md` / この skill を更新

## 参考リンク

- [ADR-0001 アーキテクチャー判断を ADR として記録する](../../adr/0001-record-architecture-decisions.md)
- [ADR-0002 クリーンアーキテクチャー風の層分割と DI コンテナの導入](../../adr/0002-clean-architecture-layered-projects.md)
- [copilot-instructions.md](../../copilot-instructions.md)
- [ADR README (運用ルール)](../../adr/README.md)
- [unit-test skill](../unit-test/SKILL.md)
