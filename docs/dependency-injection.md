# 依存性注入

`Microsoft.Extensions.DependencyInjection` を採用し、`App.Services` をプロセス共通のルート `IServiceProvider` として公開する。**ウィンドウごとに `IServiceScope`** を作成し、Window 寿命のサービスはそのスコープに紐づけて生成・破棄する。判断の経緯は [ADR-0002](../.github/adr/0002-clean-architecture-layered-projects.md) を参照。

## コンポジションルート

ルート Provider は [`ServiceProviderFactory.Build`](../src/SkimDownForWindows/Composition/ServiceProviderFactory.cs) で 1 度だけ構築される。`App.OnLaunched` 内で次の順に呼ばれる:

1. `AddSkimDownApplication()` — Application 層 (Markdown 純粋サービス, ViewModel, テーマレジストリ)
2. `AddSkimDownInfrastructure()` — Infrastructure 層 (LocalFileSystem 等)
3. Presentation 層の WindowsAppSDK 依存サービス (`IUiDispatcher`, `IWindowService`) を直接登録
4. `BuildServiceProvider(validateScopes: true)` で Scoped 違反 (Singleton → Scoped 解決) を起動時に検出

```csharp
Services = ServiceProviderFactory.Build(
    uiDispatcher,
    windowFactory: (initialActivation, restoreLastFolder) => new MainWindow(initialActivation, restoreLastFolder),
    onLastWindowClosed: ExitApp);
```

`Ioc.Default` は使わない (テスト・再初期化耐性、global static の最小化のため。詳細は ADR-0002 代替案 D)。

## 登録一覧

### Application 層: [`AddSkimDownApplication`](../src/SkimDownForWindows.Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs)

| 型 | ライフサイクル | 備考 |
|---|---|---|
| `MarkdownScanner` | Scoped | 純粋サービス。Window スコープごとに 1 つ |
| `MarkdownTreeBuilder` | Scoped | 同上 |
| `InitialSelectionPicker` | Scoped | 同上 |
| `LinkResolver` | Scoped | 同上 |
| `CommandLineLauncher` | Scoped | `Classify` 1 発のためにスコープを作る場面でも使う (App.xaml.cs 参照) |
| `MainPageViewModel` | Scoped | `IDisposable`。Window 閉鎖時に必ず dispose される |
| `ColorSchemeRegistry` | Singleton | カスタムテーマレジストリは全ウィンドウで共有する 1 状態 |

### Infrastructure 層: [`AddSkimDownInfrastructure`](../src/SkimDownForWindows.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs)

| 抽象 | 既定実装 | ライフサイクル | 備考 |
|---|---|---|---|
| (なし、Singleton クラスとして直接登録) | `SettingsFolderProvider` | Singleton | 設定 / Themes フォルダーパスを解決 |
| `IFileSystem` | `LocalFileSystem` | Singleton | |
| `IMarkdownFileReader` | `LocalMarkdownFileReader` | Singleton | UTF-8 読み込み専用 |
| `ISettingsRepository` | `JsonSettingsRepository` | Singleton | `SemaphoreSlim` で single-flight |
| `IColorSchemeSource` | `LocalColorSchemeSource` | Singleton | `<base>/Themes` の `*.json` を列挙 |
| `IClipboardService` | `WindowsClipboardService` | Singleton | |
| `IShellService` | `ExplorerShellService` | Singleton | `Reveal` のみ |
| `ISystemThemeProvider` | `UiSettingsThemeProvider` | Singleton | `Windows.UI.ViewManagement.UISettings` |
| `IExternalUriLauncher` | `LauncherExternalUriService` | Singleton | `Windows.System.Launcher` |
| `IAppInfoService` | `PackageAppInfoService` | Singleton | About ダイアログ向けメタ情報 |
| `IAppLogger` | `FileAppLogger` | Singleton | best-effort、例外を投げない |
| `IFolderWatcher` | `FileSystemFolderWatcher` | **Scoped** | `IUiDispatcher` 依存・Window 寿命 |

### Presentation 層: [`ServiceProviderFactory.Build`](../src/SkimDownForWindows/Composition/ServiceProviderFactory.cs)

| 抽象 | 実装 | ライフサイクル | 備考 |
|---|---|---|---|
| `IUiDispatcher` | `DispatcherQueueUiDispatcher` | Singleton | UI スレッドの `DispatcherQueue` をラップ |
| `IWindowService` | `WindowService` | Singleton | ウィンドウレジストリ。旧 `static class WindowManager` を置き換え |

## Ports / Adapters 対応表

Application 層の I/F (port) と Infrastructure / Presentation 層の実装 (adapter) を一覧する。シグネチャ詳細は I/F 定義ファイルが真実。

| Port (Application/Abstractions) | Adapter (実装) | 配置層 | ライフサイクル |
|---|---|---|---|
| [`IAppInfoService`](../src/SkimDownForWindows.Application/Abstractions/IAppInfoService.cs) | [`PackageAppInfoService`](../src/SkimDownForWindows.Infrastructure/Windows/PackageAppInfoService.cs) | Infrastructure | Singleton |
| [`IAppLogger`](../src/SkimDownForWindows.Application/Abstractions/IAppLogger.cs) | [`FileAppLogger`](../src/SkimDownForWindows.Infrastructure/IO/FileAppLogger.cs) | Infrastructure | Singleton |
| [`IClipboardService`](../src/SkimDownForWindows.Application/Abstractions/IClipboardService.cs) | [`WindowsClipboardService`](../src/SkimDownForWindows.Infrastructure/Windows/WindowsClipboardService.cs) | Infrastructure | Singleton |
| [`IColorSchemeSource`](../src/SkimDownForWindows.Application/Abstractions/IColorSchemeSource.cs) | [`LocalColorSchemeSource`](../src/SkimDownForWindows.Infrastructure/IO/LocalColorSchemeSource.cs) | Infrastructure | Singleton |
| [`IExternalUriLauncher`](../src/SkimDownForWindows.Application/Abstractions/IExternalUriLauncher.cs) | [`LauncherExternalUriService`](../src/SkimDownForWindows.Infrastructure/Windows/LauncherExternalUriService.cs) | Infrastructure | Singleton |
| [`IFileSystem`](../src/SkimDownForWindows.Application/Abstractions/IFileSystem.cs) | [`LocalFileSystem`](../src/SkimDownForWindows.Infrastructure/IO/LocalFileSystem.cs) | Infrastructure | Singleton |
| [`IFolderWatcher`](../src/SkimDownForWindows.Application/Abstractions/IFolderWatcher.cs) | [`FileSystemFolderWatcher`](../src/SkimDownForWindows.Infrastructure/IO/FileSystemFolderWatcher.cs) | Infrastructure | **Scoped** |
| [`IMarkdownFileReader`](../src/SkimDownForWindows.Application/Abstractions/IMarkdownFileReader.cs) | [`LocalMarkdownFileReader`](../src/SkimDownForWindows.Infrastructure/IO/LocalMarkdownFileReader.cs) | Infrastructure | Singleton |
| [`ISettingsRepository`](../src/SkimDownForWindows.Application/Abstractions/ISettingsRepository.cs) | [`JsonSettingsRepository`](../src/SkimDownForWindows.Infrastructure/IO/JsonSettingsRepository.cs) | Infrastructure | Singleton |
| [`IShellService`](../src/SkimDownForWindows.Application/Abstractions/IShellService.cs) | [`ExplorerShellService`](../src/SkimDownForWindows.Infrastructure/Windows/ExplorerShellService.cs) | Infrastructure | Singleton |
| [`ISystemThemeProvider`](../src/SkimDownForWindows.Application/Abstractions/ISystemThemeProvider.cs) | [`UiSettingsThemeProvider`](../src/SkimDownForWindows.Infrastructure/Windows/UiSettingsThemeProvider.cs) | Infrastructure | Singleton |
| [`IUiDispatcher`](../src/SkimDownForWindows.Application/Abstractions/IUiDispatcher.cs) | [`DispatcherQueueUiDispatcher`](../src/SkimDownForWindows/Composition/DispatcherQueueUiDispatcher.cs) | Presentation | Singleton |
| [`IWindowService`](../src/SkimDownForWindows.Application/Abstractions/IWindowService.cs) (+ `IWindowHandle`) | [`WindowService`](../src/SkimDownForWindows/Composition/WindowService.cs) (+ [`MainWindowHandle`](../src/SkimDownForWindows/Composition/MainWindowHandle.cs)) | Presentation | Singleton |

`IUiDispatcher` と `IWindowService` だけが Presentation 層 (App プロジェクト) で実装されているのは、それぞれ `Microsoft.UI.Dispatching.DispatcherQueue` と `Microsoft.UI.Xaml.Window` という WindowsAppSDK 型を必要とするため。Infrastructure は WinRT + `System.IO` のみで完結する。

## ウィンドウスコープのライフサイクル

`MainWindow` のコンストラクタで `App.Services.CreateScope()` を呼び、`IServiceScope` をフィールドに保持する。閉じる時に `OnClosed` で dispose する。

```csharp
public MainWindow(InitialActivation? initialActivation, bool restoreLastFolder)
{
    InitializeComponent();
    _scope = App.Services.CreateScope();
    // ...
    var startArgs = new MainPageStartArgs(this, _scope.ServiceProvider, initialActivation, restoreLastFolder);
    RootFrame.Navigate(typeof(MainPage), startArgs);
}

private void OnClosed(object sender, WindowEventArgs args)
{
    try { _scope.Dispose(); }
    catch { /* best-effort */ }
}
```

`MainPage` は XAML が要求するパラメーターレス ctor を持つため DI に乗らない。`OnNavigatedTo` で `MainPageStartArgs.ScopeProvider.GetRequiredService<MainPageViewModel>()` でスコープから VM を取得する。

`MainPageViewModel : IDisposable` の `Dispose` で `IFolderWatcher.Dispose()` と購読解除を行う。Scope dispose が VM dispose を呼び、VM dispose が watcher dispose を呼ぶ連鎖により、ウィンドウ閉鎖時の `FileSystemWatcher` リークが構造的に防止される。

## `validateScopes: true` の意味

`BuildServiceProvider(validateScopes: true)` を渡すと、`IServiceProvider` を構築した瞬間に **Singleton → Scoped の解決が静的に検証**される。例えば誤って `ISettingsRepository` の実装から `IFolderWatcher` (Scoped) をコンストラクタ注入した場合、アプリ起動時点で例外が出る。Release ビルドでも検証される。

注意点: `App.Services` (ルート Provider) は **ルートスコープ** とみなされるので、ルートから直接 `GetRequiredService<MainPageViewModel>()` を呼ぶと scope 違反になる。VM を欲しい場面では必ず `App.Services.CreateScope()` で子スコープを掘ってから resolve する (例: [`App.OpenFirstWindowFromActivation`](../src/SkimDownForWindows/App.xaml.cs) は `CommandLineLauncher` を子スコープから取る)。

## サービス生成タイミングのまとめ

| サービス | いつ作られるか | いつ捨てられるか |
|---|---|---|
| `ISettingsRepository` ほか Singleton | `ServiceProviderFactory.Build` 直後の初回 resolve 時 | アプリ終了時 |
| `ColorSchemeRegistry` | 同上 (Singleton) | 同上 |
| `MainPageViewModel` | Window が `MainPage.OnNavigatedTo` で VM を要求した時 | Window の `OnClosed` → `_scope.Dispose()` で `Dispose()` が呼ばれる |
| `IFolderWatcher` | VM のコンストラクタ注入時 | VM dispose 時に明示的に `Dispose()` が呼ばれる |
| Markdown 純粋サービス | VM のコンストラクタ注入時 | スコープ dispose 時 (Dispose 不要なので無処理) |
| `CommandLineLauncher` | スコープから resolve された時 | スコープ dispose 時 |

## 関連

- ADR: [0002 クリーンアーキテクチャー風の層分割と DI](../.github/adr/0002-clean-architecture-layered-projects.md)
- skill: [`clean-architecture/SKILL.md`](../.github/skills/clean-architecture/SKILL.md) (Singleton vs Scoped の判断・新規サービス追加手順)
- 隣接ドキュメント: [`architecture.md`](architecture.md), [`activation-and-single-instance.md`](activation-and-single-instance.md)
- コード: [`ServiceProviderFactory.cs`](../src/SkimDownForWindows/Composition/ServiceProviderFactory.cs), [`ApplicationServiceCollectionExtensions.cs`](../src/SkimDownForWindows.Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs), [`InfrastructureServiceCollectionExtensions.cs`](../src/SkimDownForWindows.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs)
