# Markdown コンテンツパイプライン

「**フォルダーを開いてから、ツリーが描かれ、ファイルが選ばれて、プレビューに描画される**」までと、その後の **変更検知 / リロード** までの一連のフローを記述する。すべて Application 層で完結しており、外部 I/O は I/F (`IFileSystem`, `IMarkdownFileReader`, `IFolderWatcher`) 経由のみで行う。

## 概観

```mermaid
flowchart TB
    subgraph User_input["ユーザー / 起動"]
        UA["File &gt; Open Folder<br/>CLI 引数 / Drag-drop"]
        UF["File activation<br/>(.md ダブルクリック)"]
    end

    subgraph App_layer["Application 層"]
        VM["MainPageViewModel<br/>(Scoped)"]
        SC["MarkdownScanner<br/>(Scoped)"]
        TB["MarkdownTreeBuilder<br/>(Scoped)"]
        RB["RecentMarkdownListBuilder<br/>(Scoped)"]
        PK["InitialSelectionPicker<br/>(Scoped)"]
        LR["LinkResolver<br/>(Scoped)"]
        WT["IFolderWatcher<br/>(Scoped)"]
    end

    subgraph Infra_layer["Infrastructure / OS"]
        FS["IFileSystem<br/>(LocalFileSystem)"]
        MR["IMarkdownFileReader<br/>(LocalMarkdownFileReader)"]
        FSW["FileSystemWatcher"]
    end

    subgraph Presentation["Presentation"]
        Preview["MarkdownPreview<br/>(WebView2)"]
    end

    UA -->|OpenFolderAsync| VM
    UF -->|OpenSingleFileAsync| VM
    VM -->|Scan| SC
    SC --> FS
    VM -->|Build (tree)| TB
    VM -->|Build (recent)| RB
    RB --> FS
    VM -->|Pick| PK
    VM -->|ReadAsync| MR
    VM -->|PreviewLoadRequested<br/>(LoadRequest)| Preview
    VM -->|Watch| WT
    WT --> FSW
    FSW -->|TreeMayHaveChanged<br/>FileContentChanged| WT
    WT -->|UI thread marshal| VM
    Preview -->|RelativeMarkdownLinkClicked| VM
    VM -->|Classify| LR
```

## フォルダー走査 (`MarkdownScanner`)

[`MarkdownScanner.Scan(rootFolderPath)`](../src/SkimDownForWindows.Application/Markdown/MarkdownScanner.cs) は次のルールでフラットなファイルパス一覧を返す:

- 対象拡張子: `.md`, `.markdown` (大文字小文字を区別しない)
- 再帰: 深さ無制限で `IFileSystem.EnumerateFileSystemEntries` を辿る
- 除外フォルダー (どの深さでも): `.git`, `node_modules`, `.build`, `DerivedData`
- 除外: ファイル名が `.` で始まるエントリ、または `IFileSystem.IsHiddenOrSystem(path)` が `true` のエントリ
- 並び順は保証されない (ソートは `MarkdownTreeBuilder` の責務)
- アクセス不能なフォルダーは `IFileSystem` 側で空列挙されるため、例外は伝播しない

## ツリー化 (`MarkdownTreeBuilder`)

[`MarkdownTreeBuilder.Build(rootFolderPath, files)`](../src/SkimDownForWindows.Application/Markdown/MarkdownTreeBuilder.cs) は次のルールで階層 `MarkdownTreeItem` を組み立てる:

- フォルダー優先 → ファイルの順 (VS Code Explorer 互換)
- 同階層内は大文字小文字を区別しないアルファベット順
- 中身に Markdown を 1 つも含まないフォルダーは結果に含めない
- 相対パスは **forward-slash** で正規化 (Windows でもツリーの相対 key は `/` 区切り)
- ルートノードの `MarkdownCount` に総 Markdown 数を入れる (サイドバー上部の件数表示用)

`MarkdownTreeItem` は `ObservableObject` で、UI 側 (`TreeView`) は `Children` / `IsExpanded` をバインドする。

## サイドバー表示モード (`SidebarViewMode`)

サイドバーのファイル一覧は 2 つの表示モードを持つ。モードは [`MainPageViewModel.SidebarViewMode`](../src/SkimDownForWindows.Application/ViewModels/MainPageViewModel.cs) が保持し、[`AppSettings.SidebarViewMode`](../src/SkimDownForWindows.Application/Models/AppSettings.cs) でグローバルに永続化される ([settings-and-state.md](settings-and-state.md) 参照)。

| `SidebarViewMode` | ビルダー | 構造 | 並び順 |
|---|---|---|---|
| `Tree` (既定) | `MarkdownTreeBuilder` | フォルダー階層ツリー | フォルダー優先 → 名前昇順 |
| `RecentlyModified` | `RecentMarkdownListBuilder` | 全 Markdown のフラット 1 段リスト (leaf のみ) | 更新日時の新しい順 |

`MainPageViewModel.BuildRoot(folder)` が現在のモードに応じてどちらかのビルダーを呼び、どちらも `Children` と `MarkdownCount` を持つ root `MarkdownTreeItem` を返すため、後段 (`ReplaceRoot` / 選択同期) の流れは共通になる。両モードとも単一の `TreeView` を再利用する (フラット側は leaf を root 直下に並べるだけ)。

モード切り替えは [`MainPageViewModel.SetSidebarViewModeAsync(mode)`](../src/SkimDownForWindows.Application/ViewModels/MainPageViewModel.cs) (`SetSidebarViewModeCommand`) で行う:

- `Tree` から離れる前に現在の展開状態を `FolderState.ExpandedFolders` に退避する (`RecentlyModified` 中は展開状態を保存しない)。
- 再構築 (`BuildRoot` + `ReplaceRoot`) は `await` を挟まず同期適用するため、watcher 由来の `OnTreeMayHaveChanged` と競合しても古い結果で上書きされない。
- 現在選択中ファイルの相対パスを引き継ぎ、再構築後の新インスタンス上で再選択する。
- `AppSettings.SidebarViewMode` を更新して `ISettingsRepository.SaveAsync` する。

### 更新日順の一覧 (`RecentMarkdownListBuilder`)

[`RecentMarkdownListBuilder.Build(rootFolderPath, files)`](../src/SkimDownForWindows.Application/Markdown/RecentMarkdownListBuilder.cs) は次のルールでフラット root を組み立てる:

- フォルダー階層は作らず、配下の全 Markdown を root 直下の leaf として並べる
- 各 leaf の最終更新日時は [`IFileSystem.GetLastWriteTimeUtc`](../src/SkimDownForWindows.Application/Abstractions/IFileSystem.cs) で取得し、`MarkdownTreeItem.LastModified` に設定する (取得失敗時は `DateTimeOffset.MinValue`)
- 並び順は決定的: 更新日時の新しい順 (降順) → 名前昇順 → 相対パス昇順 (いずれも大文字小文字を区別しない)
- 各 leaf の `MarkdownTreeItem.RelativeFolder` に親フォルダーの相対パス (forward-slash) を入れる。ルート直下のファイルは空文字
- ルートノードの `MarkdownCount` に総 Markdown 数を入れる

UI 側は `LastModified` と `RelativeFolder` を一覧行の 2 行目 (日時 + フォルダー) に表示し、`LastModified` が `null` の場合 (= `Tree` モードの leaf) は 2 行目を出さない。

## 初期選択 (`InitialSelectionPicker`)

フォルダーを開いた直後に表示するファイルは [`MainPageViewModel.PickInitialSelection`](../src/SkimDownForWindows.Application/ViewModels/MainPageViewModel.cs) がモードに応じて決定する。

`Tree` モードは [`InitialSelectionPicker.Pick`](../src/SkimDownForWindows.Application/Markdown/InitialSelectionPicker.cs) に委譲する。優先順:

1. 前回そのフォルダーで開いていた Markdown (`FolderState.LastSelectedRelativePath` で指定。実在チェック付き)
2. ルート直下の `README.md` または `README.markdown` (大文字小文字を区別しない)
3. ツリー深さ優先で見つかる最初の Markdown
4. 該当無し → empty 状態 (`HasAnyMarkdown=false`)

`RecentlyModified` モードは README を優先せず、「前回選択 (相対パス一致) → 先頭 (= 最新ファイル)」の順で決める。

## 読込・描画リクエスト

[`MainPageViewModel.SelectAndLoadAsync(absolutePath)`](../src/SkimDownForWindows.Application/ViewModels/MainPageViewModel.cs):

1. `OpenedFolderPath` 配下チェック (`PathHelpers.IsInsideFolder`)
2. `IMarkdownFileReader.ReadAsync` で UTF-8 読込 (失敗時はエラー Markdown を返すため例外は出ない)
3. `EffectiveTheme()` で `"light"` / `"dark"` を決定
4. `PreviewLoadRequested?.Invoke(new LoadRequest(text, relativePath, theme))` を発火
5. `FolderState.LastSelectedRelativePath` を更新して `ISettingsRepository.SaveAsync`

Presentation 層 (`MainPage.xaml.cs`) は `PreviewLoadRequested` を購読しており、`MarkdownPreview.LoadAsync(...)` に転送する。データの WebView2 への到達方法は [webview2-preview.md](webview2-preview.md) を参照。

## リンクの分類 (`LinkResolver`)

レンダラーがリンクをクリックすると `MarkdownPreview` 側から host (`MainPage`) にメッセージが届く。host は `LinkResolver.Classify(folderRoot, originFilePath, href)` で `LinkClassification` を得る。

| `LinkKind` | 該当ケース | host の処理 |
|---|---|---|
| `Anchor` | `#section` | レンダラーに `scrollToAnchor` を送り返す |
| `RelativeMarkdown` | フォルダー内の `.md` / `.markdown` | folder mode: 当該ファイルを `SelectAndLoadAsync` / single-file mode: 新規 single-file ウィンドウ |
| `RelativeNonMarkdown` | フォルダー内の非 Markdown ローカルファイル | ブロック (操作なし) |
| `OutOfFolder` | 開いているフォルダーの外を指す相対パス | ブロック |
| `External` | `http://` / `https://` | `IExternalUriLauncher.LaunchAsync` |
| `Blocked` | `javascript:`, `mailto:`, 不正 URL 等 | ブロック |

副作用ゼロ (内部で IO 呼ばない)。テストは [`LinkResolverTests`](../src/SkimDownForWindows.Tests/LinkResolverTests.cs)。

## 変更検知 (`IFolderWatcher`)

ウィンドウごとに 1 つの `IFolderWatcher` (Scoped) を `MainPageViewModel` が購読する。

実装 [`FileSystemFolderWatcher`](../src/SkimDownForWindows.Infrastructure/IO/FileSystemFolderWatcher.cs) は `FileSystemWatcher` をラップし、次のイベントを発火する:

| イベント | 何が起きた時 | debounce | 引数 |
|---|---|---|---|
| `TreeMayHaveChanged` | Markdown パスの Created / Renamed、ディレクトリの Created / Renamed、Deleted、`FileSystemWatcher.Error` (バッファオーバーフローのフォールバック) | 250ms (`TreeDebounce`) | なし |
| `FileContentChanged` | `.md` / `.markdown` ファイルの Changed | なし (即時) | 絶対パス |

両イベントとも UI スレッドに `IUiDispatcher.TryEnqueue` で marshal してから発火されるので、購読者 (VM) は UI スレッド前提でハンドラーを書ける。

設定: `IncludeSubdirectories = true`、`InternalBufferSize = 64 * 1024`、`NotifyFilter = FileName | DirectoryName | LastWrite | Size`。

Created / Renamed のうち、Markdown でもディレクトリでもない通常ファイルのイベントは `TreeMayHaveChanged` に変換されない。Deleted は OS から削除後のパスだけが届き、ファイル / ディレクトリの種別を安全に判定できないため、ツリー再走査対象として扱う。

### VM の取り扱い ([`MainPageViewModel.OnTreeMayHaveChanged` / `OnFileContentChanged`](../src/SkimDownForWindows.Application/ViewModels/MainPageViewModel.cs))

| モード | `TreeMayHaveChanged` | `FileContentChanged` |
|---|---|---|
| folder mode | 再走査後のツリーが現在の `RootItems` と等価なら no-op。差分がある場合は現在の展開状態と選択状態を保持したまま再構築 → 復元 | 当該ファイルが今 selected なら再読込 |
| single-file mode | **何もしない** (ツリーを使わないので再走査不要) | 監視対象ファイルと一致すれば `ReloadSingleFileAsync` |

## folder mode と single-file mode の差分

| 項目 | folder mode | single-file mode |
|---|---|---|
| 入口 API | `OpenFolderAsync(folderPath)` | `OpenSingleFileAsync(filePath)` |
| `IsSingleFileMode` | `false` | `true` |
| `RootItems` | スキャン結果のツリー | 空 (synthetic な `SelectedItem` のみ生成) |
| `HasAnyMarkdown` | スキャン結果次第 | `true` 固定 (empty overlay を出さない) |
| `OpenedFolderPath` | 開いたフォルダー | **対象ファイルの親フォルダー** (相対画像 / 相対リンク解決のための base) |
| `MarkdownCount` | スキャン総数 | `1` |
| `ISettingsRepository` 更新 | `RecentFolders`, `LastFolderPath`, `FolderState`, `SidebarVisible` (UI 経由) すべて更新 | **永続化キーは一切更新しない** ([settings-and-state.md](settings-and-state.md) 参照) |
| `IFolderWatcher.Watch` | 開いたフォルダー | 対象ファイルの親フォルダー |
| `TreeMayHaveChanged` の反応 | tree 再走査 (等価なら no-op) | 無視 |
| 相対 Markdown リンク | tree 選択を切替 | 新規 single-file ウィンドウ |

判断の経緯は [ADR-0005](../.github/adr/0005-single-file-mode-and-file-activation.md) を参照。

## 関連

- ADR: [0005 Single-file mode と File Activation の導入](../.github/adr/0005-single-file-mode-and-file-activation.md), [0002 クリーンアーキテクチャー風の層分割と DI](../.github/adr/0002-clean-architecture-layered-projects.md)
- SPEC: [`design/SPEC.md`](../design/SPEC.md) の「ファイル検出」「ツリー」「初期選択」「変更検知」「単一ファイルを開く」セクション
- skill: [`unit-test/SKILL.md`](../.github/skills/unit-test/SKILL.md) (`MarkdownScanner` 等のテストパターン)
- 隣接ドキュメント: [`webview2-preview.md`](webview2-preview.md), [`settings-and-state.md`](settings-and-state.md), [`activation-and-single-instance.md`](activation-and-single-instance.md)
- コード: [`MarkdownScanner.cs`](../src/SkimDownForWindows.Application/Markdown/MarkdownScanner.cs), [`MarkdownTreeBuilder.cs`](../src/SkimDownForWindows.Application/Markdown/MarkdownTreeBuilder.cs), [`RecentMarkdownListBuilder.cs`](../src/SkimDownForWindows.Application/Markdown/RecentMarkdownListBuilder.cs), [`InitialSelectionPicker.cs`](../src/SkimDownForWindows.Application/Markdown/InitialSelectionPicker.cs), [`LinkResolver.cs`](../src/SkimDownForWindows.Application/Markdown/LinkResolver.cs), [`FileSystemFolderWatcher.cs`](../src/SkimDownForWindows.Infrastructure/IO/FileSystemFolderWatcher.cs), [`MainPageViewModel.cs`](../src/SkimDownForWindows.Application/ViewModels/MainPageViewModel.cs)
