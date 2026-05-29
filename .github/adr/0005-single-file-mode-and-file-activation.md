# 0005. Single-file mode と File Activation の導入

- 日付: 2026-05-29
- ステータス: Proposed
- 関連 ADR: [0002](0002-clean-architecture-layered-projects.md)

## コンテキスト

SkimDown for Windows は上流 macOS 版 ([07JP27/SkimDown](https://github.com/07JP27/SkimDown)) の挙動互換を目標としているが、上流が持つ **single-file mode** が未実装だった。Single-file mode は次のような起動シナリオで必要になる:

- Explorer で `.md` をダブルクリックして個別ファイルを開きたい
- `skimdown README.md` のように CLI で 1 ファイル指定
- エディタ等から `.md` を SkimDown 関連付けで「プログラムから開く」

上流の挙動は次の通り:

1. サイドバー (ツリー) は強制非表示、表示するのは指定ファイル 1 件
2. ウィンドウタイトルは `<filename> — SkimDown`
3. RecentFolders / LastFolderPath / 「最後に開いていたフォルダー」永続状態は更新しない
4. 既に空 / single-file mode ウィンドウがあれば 1 つだけ再利用、残りは新規ウィンドウ
5. 親フォルダーを `FileSystemWatcher` で監視するが、ツリー再走査はしない。対象ファイルの content 変更のみ reload
6. Toggle / Move Sidebar は無効化
7. Open Folder で folder mode に戻ると、永続設定通りにサイドバーが復元される

これに加えて Windows 固有の検討点として:

- **Explorer ダブルクリック** を有効にするには `Package.appxmanifest` の `windows.fileTypeAssociation` 拡張で `.md` / `.markdown` を SkimDown に紐付ける必要がある
- 複数ファイルの "Open with → SkimDown" や、起動中にもう 1 つダブルクリックすると、デフォルトでは **新しいプロセス** が起動してしまう。settings.json への書き込み競合が発生し得る
- 既存の `CommandLineLauncher.TryGetInitialFolderPath` は、`.md` 引数を受けると親フォルダーを開く挙動だった。これは folder mode 専用の API であり、single-file mode を返す表現力がない

これらをまとめて、上流と挙動互換でかつ Windows のプロセスモデルとも整合する設計が必要になった。

## 決定

次の 4 つを一体として導入する。

### 1. `InitialActivation` discriminated record による activation 表現

`Application/Models/InitialActivation.cs` を新設し、次の派生を持つ:

```csharp
public abstract record InitialActivation;
public sealed record OpenFolderActivation(string FolderPath) : InitialActivation;
public sealed record OpenSingleFileActivation(string FilePath) : InitialActivation;
```

これを `CommandLineLauncher`・`MainPageStartArgs`・`IWindowService` の共通言語とし、folder / single-file の二択を型レベルで表す。

`CommandLineLauncher` は次の 2 つの公開メソッドを持つ:

- `TryResolveActivation(string[] args, string cwd) → InitialActivation?` — CLI 引数解析
- `Classify(string path, string cwd) → InitialActivation?` — 1 個のパスを folder / single-file / null に分類 (File activation で複数ファイルを処理する時に各パスに対して呼ぶ)

### 2. `MainPageViewModel.OpenSingleFileAsync` の dedicated load 経路

Folder mode 用の `OpenFolderAsync` / `SelectAndLoadAsync` を流用すると、上流仕様で禁じられている永続化 (RecentFolders / LastFolderPath / FolderState の更新) を起こしてしまう。そのため single-file mode 用の load / reload 経路を分離する:

- `[ObservableProperty] IsSingleFileMode`
- `OpenSingleFileAsync(filePath)`: synthetic な `MarkdownTreeItem` を生成して `SelectedItem` に割り当てる (RootItems[] は空のまま)。`UpdateRecentFolders` / `SaveAsync` / `GetOrCreateFolderState` を一切呼ばない
- `private ReloadSingleFileAsync()`: file watcher の content change で呼ばれる単純な reread + `PreviewLoadRequested` 発火 (永続化なし)
- `OnTreeMayHaveChanged` は single-file mode で no-op
- `OnFileContentChanged` は single-file mode 時に対象ファイルのみ `ReloadSingleFileAsync` に分岐

### 3. 自前 `Program.Main` による single-instance redirect

WinUI 3 が自動生成する `Main` を `<DefineConstants>DISABLE_XAML_GENERATED_MAIN</DefineConstants>` で抑止し、`SkimDownForWindows/Program.cs` を新設する。

```csharp
[STAThread]
static int Main(string[] args)
{
    WinRT.ComWrappersSupport.InitializeComWrappers();
    var thisInstance = AppInstance.GetCurrent();
    var mainInstance = AppInstance.FindOrRegisterForKey("SkimDownForWindowsMain");
    if (!mainInstance.IsCurrent)
    {
        mainInstance.RedirectActivationToAsync(thisInstance.GetActivatedEventArgs())
            .AsTask().GetAwaiter().GetResult();
        return 0;
    }
    thisInstance.Activated += App.OnRedirectedActivation;
    Application.Start(p =>
    {
        var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
        SynchronizationContext.SetSynchronizationContext(ctx);
        _ = new App();
    });
    return 0;
}
```

これにより 2 回目以降の Explorer ダブルクリック / CLI 起動は **既存プロセス** に redirect され、settings.json への並行書き込み競合が原理的に発生しなくなる。

`App` 側は `OnRedirectedActivation` ハンドラと「pending queue → `OnLaunched` 完了後に drain」のロジックを持ち、UI / DI が ready になる前の redirect を取りこぼさない。

### 4. サイドバー visual state を Presentation 層で集中管理

永続設定 `AppSettings.SidebarVisible` は **folder mode 用の真実** として保持し、single-file mode は code-behind の `ApplySidebarVisualState()` ヘルパーで **visual override** として隠す。`IsSingleFileMode` の `PropertyChanged` を `OnViewModelPropertyChanged` で受けて再適用する。Toggle/Move Sidebar は XAML の `IsEnabled={x:Bind IsNotSingleFileMode(ViewModel.IsSingleFileMode), Mode=OneWay}` で無効化しつつ、keyboard accelerator / 外部経路への防御として `OnToggleSidebarClick` / `OnMoveSidebarClick` 先頭でも `if (ViewModel.IsSingleFileMode) return;` ガードを置く。

加えて:

- **WindowService**: `OpenSingleFile(path)` (1 個目だけ再利用) / `OpenSingleFileInNewWindow(path)` / `OpenSingleFilesInWindows(paths)` (batch) を追加。再利用ポリシーは `WindowService` 内に閉じ込め、Presentation 側は意図を API で明示
- **MainWindow**: `GetViewModel()` / `IsEmptyOrSingleFile` ヘルパーを追加し、別ウィンドウからの再利用判定を可能に
- **Package.appxmanifest**: `windows.fileTypeAssociation` で `.md` / `.markdown` を `<DisplayName>Markdown Document</DisplayName>` として関連付け (`OpenIsSafe="true"`)
- **Relative markdown link in single-file mode**: 同 mode の不変条件「1 ファイル 1 ウィンドウ」を守るため、folder mode の `SelectAndLoadAsync` には流さず `IWindowService.OpenSingleFileInNewWindow(target)` で新 single-file ウィンドウを開く
- **Drag-drop**: `.md` ファイル drop は single-file mode として扱う。空ウィンドウなら現ウィンドウに load、それ以外は新規ウィンドウ

## 結果（Consequences）

### ポジティブ

- 上流 macOS 版とユーザー体験が揃う (Explorer ダブルクリック / CLI / drag-drop 全経路で single-file mode に入る)
- `settings.json` の並行書き込み競合が起こらない (single-instance)
- 永続設定が single-file mode の visual override で壊れない (= folder mode に戻った時の UX が安定)
- `InitialActivation` で activation の意図が型レベルで表現され、`CommandLineLauncher` の API が明確になる
- 単体テストが `InitialActivation` ベースで書けるため、Activation の分類ロジックを WinUI 統合なしに検証可能

### ネガティブ

- `Program.Main` を自前で書く責任が生じる。WinUI 3 の generated Main が行う COM / sync context 初期化を正確に再現しないと、XAML 例外で起動失敗する
- `OpenFolderAsync` / `OpenSingleFileAsync` の 2 つの load 経路が併存するため、永続化に関する不変条件は **テストで保証** する必要がある (CI に乗せた)
- Activation redirect は `OnLaunched` より前にも届く可能性があるため、pending queue + drain ロジックを保守する必要がある

### ニュートラル

- `MainPageStartArgs` のシグネチャが `string? InitialFolderPath` から `InitialActivation? InitialActivation` に変わる (内部 API なので影響は限定的)
- `IWindowService` API が増える (`OpenSingleFile` / `OpenSingleFileInNewWindow` / `OpenSingleFilesInWindows`)

## 検討した代替案

### 代替案 A: 別プロセスを起動するだけのナイーブ実装

- 概要: single-instance 化せず、Explorer ダブルクリックや CLI で `.md` を渡すたびに新規プロセスを起動。`OnLaunched` で activation 引数を見て single-file mode を開く
- 採用しなかった理由:
  - settings.json への並行書き込み競合が発生し得る (`JsonSettingsRepository` は単一プロセス前提)
  - 上流 macOS では「既存の空 / single-file ウィンドウを再利用」する振る舞いだが、別プロセスではウィンドウ間で状態を共有できない
  - ユーザーから見るとタスクバーに同じアプリの複数アイコンが並ぶ UX 上の不一致

### 代替案 B: generated Main を維持し `AppInstance` を `App.OnLaunched` 内で呼ぶ

- 概要: `DISABLE_XAML_GENERATED_MAIN` を使わず、`App.OnLaunched` の最初で `AppInstance.FindOrRegisterForKey` してリダイレクト判定する
- 採用しなかった理由:
  - `Application.Start` で App が構築された後に redirect 判定するのは「遅すぎる」: 二次インスタンスでも一度 WinUI ランタイムが起動し、無駄な COM / リソース確保が起こる
  - 公式ドキュメントの推奨パターンは「`Application.Start` 前に redirect 判定」

### 代替案 C: 上流方式 (`settings.isSidebarVisible = false; savedSidebarVisible = ...`) で永続設定そのものを書き換える

- 概要: Single-file mode 中は `Settings.Current.SidebarVisible = false` を直接書き、in-memory な `savedSidebarVisible` で元値を保持。folder mode に戻る時に書き戻す
- 採用しなかった理由:
  - Single-file mode で crash / プロセス終了すると、`SidebarVisible = false` が永続化されてしまう。次回 folder mode で起動した時にサイドバーが消えていて UX が壊れる
  - 「永続設定 = folder mode 用の真実、single-file は visual override」と切り分けたほうが、データモデル上の責務が明確
  - 上流 macOS の選択は「設定値が live source-of-truth」という前提に基づく。Windows 版では `JsonSettingsRepository` を単一の真実とする方針と整合させた

### 代替案 D: `.md` CLI 引数を「親フォルダーを開く」現行挙動のまま残す

- 概要: 既存の `CommandLineLauncher.TryGetInitialFolderPath` 挙動を維持し、ファイル関連付けからも folder mode で開く
- 採用しなかった理由:
  - 「READ.md を開きたい」のに兄弟ファイルを含むツリーが副作用で見えるのは、Markdown を AI エージェント生成ログとして読みたい本来のユースケースで邪魔
  - 上流挙動と非互換になり、ドキュメント・期待値が分岐する

## 参考リンク

- [上流 macOS 版 SkimDown](https://github.com/07JP27/SkimDown)
- [Make the app single-instanced (Windows App SDK)](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/guides/applifecycle#make-the-app-single-instanced)
- [File type associations (UWP / MSIX)](https://learn.microsoft.com/windows/uwp/launch-resume/handle-file-activation)
- ADR [0002](0002-clean-architecture-layered-projects.md): クリーンアーキテクチャー風プロジェクト分割 (本 ADR の Application/Presentation 層境界の根拠)
