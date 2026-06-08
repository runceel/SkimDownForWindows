# SkimDown for Windows — MVP仕様

本書は上流 [`07JP27/SkimDown/design/SPEC.md`](https://github.com/07JP27/SkimDown/blob/main/design/SPEC.md) を Windows 向けに移植した仕様書です。章構成は原則として上流に合わせ、Windows 固有の項目 (single-file mode / single-instance / WebView2 origin 分離 / Themes フォルダー / Microsoft Store 配布など) はセクション追加または本文追記で取り込みます。

実装上の設計判断 (なぜそうしたか) は [`.github/adr/`](../.github/adr/) を、リポジトリ全体のコーディング指針は [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) を参照してください。

## 前提

- Windows 10 v1903 以降 (Windows 11 推奨)
- Developer Mode 有効 (sideload や `winapp` 実行のため)
- 通常配布の Win32 アプリ (Microsoft Store 配布 + sideload 用 MSIX。MSIX 内では packaged identity を持つ)
- 読み取り専用 Markdown ビューアー
- 複数ウィンドウ対応 (ただしプロセスは単一)
- ユーザーが選択したフォルダーまたは引数で渡されたファイル / フォルダーのみ読み取り
- GUI フレームワーク: WinUI 3 + Windows App SDK 2.0.1 + WebView2
- 言語 / ランタイム: C# / .NET 10
- MVVM: CommunityToolkit.Mvvm、DI: Microsoft.Extensions.DependencyInjection
- 通信は行わない。ローカル / 外部画像と外部リンク (既定ブラウザ起動) を除く

## フォルダを開く

- 起動時は、復元可能な前回フォルダーがあれば開く。なければ空ウィンドウを表示する
- `File > Open Folder...` または空状態の `Open Folder...` ボタンからフォルダーを開く
- `Ctrl + O` でフォルダー選択ダイアログを開く
- 最近開いたフォルダーは `File > Open Recent` に表示する (最大 16 件)
- 空ウィンドウへフォルダーをドロップした場合、そのウィンドウで開く
- すでにフォルダーを開いているウィンドウへフォルダーをドロップした場合、新しいウィンドウで開く
- `Ctrl + N` で新規空ウィンドウを開く
- コマンドラインから `skimdown <folder>` (例: `skimdown .` / `skimdown D:\notes`) で指定フォルダーを開く。`skimdown` は MSIX install 時に自動登録される実行エイリアス

## 単一ファイルを開く (single-file mode)

Markdown ファイルを直接開く `single-file mode` を提供する。フォルダーを開くケースと同じくらい一級の起動経路として扱う。詳細な設計判断は [ADR-0005](../.github/adr/0005-single-file-mode-and-file-activation.md) を参照。

### 起動経路

- **Explorer ダブルクリック**: `.md` / `.markdown` を右クリック → `Open with → SkimDown` (一度選ぶと既定にできる)
- **コマンドライン**: `skimdown README.md`
- **Drag-drop**: `.md` ファイルをウィンドウへドロップ。空ウィンドウであれば現ウィンドウで開き、folder mode / 別の single-file が表示中であれば新ウィンドウで開く
- **複数ファイル**: Explorer で複数 `.md` を選択し `Open with → SkimDown` を実行すると、各ファイルごとに 1 ウィンドウで開く

### 振る舞い

- サイドバー (フォルダーツリー) は強制的に非表示。プレビューがウィンドウ全幅を占める
- ウィンドウタイトルは `<filename> — SkimDown` (例: `README.md — SkimDown`)
- `View > Toggle Sidebar` (`Ctrl + B`) と `View > Move Sidebar ...` は無効化される
- 親フォルダーは `FileSystemWatcher` で監視され、外部編集で対象ファイルが更新されると preview が自動再読込される (tree 再走査は走らない)
- 永続設定の RecentFolders / LastFolderPath / FolderStates は **更新しない** (Explorer ダブルクリックでフォルダー履歴を汚さない)
- 永続設定の `SidebarVisible` は **書き換えない** (folder mode 用の真実として保持)。`File > Open Folder...` で folder mode に戻ると、永続設定通りにサイドバー表示状態が復元される
- 相対 Markdown リンク (`[next](./next.md)`) は **新しい single-file ウィンドウ** で開く (1 ファイル 1 ウィンドウの不変条件を保つため、folder mode のツリー選択経路には流さない)

## ファイル検出

- 対象拡張子は `.md`, `.markdown` (大文字小文字を区別しない)
- 開いたフォルダー配下を再帰的に走査する
- `.git`, `node_modules`, `.build`, `DerivedData` は除外する (どの深さでも)
- 隠しファイル / 隠しフォルダーは表示しない (名前が `.` で始まるもの、および Windows の Hidden / System 属性が立っているもの)
- Markdown を含まない空フォルダーはツリーに表示しない
- 画像ファイルは単体ではツリーに表示しない
- Markdown から参照される画像は本文内で表示する

## ツリー

- VS Code の Explorer に近い表示と並び順にする
- フォルダーを先、ファイルを後に表示する
- 名前順、大文字小文字を区別しない
- 選択中ファイルをハイライトする
- フォルダーの開閉状態はフォルダーごとに保存する (永続化対象)
- サイドバー上部には開いているフォルダー名と Markdown ファイル数だけを表示する
- サイドバー幅はドラッグで変更でき、保存する
- サイドバーは左 / 右を切り替えられる (`View > Move Sidebar to Right / Left`)
- サイドバーは表示 / 非表示を切り替えられる (`View > Toggle Sidebar`、`Ctrl + B`)

## 初期選択

フォルダーを開いた直後は、次の順で表示する Markdown を決める。

1. 前回そのフォルダーで開いていた Markdown
2. `README.md`
3. ツリー内の先頭 Markdown (フォルダーツリーを深さ優先で走査した順)
4. Markdown が無いフォルダーは「Markdown が無い」状態を表示する

## プレビュー

- WebView2 で描画する
- 本文は左揃え
- 本文カラムは読みやすい幅に収め、左右に十分な余白を持たせる
- 画面が狭い場合は自然に縮む
- ライト / ダーク / System / カスタムテーマに対応する (詳細は [カラーテーマ](#カラーテーマ))
- ズーム倍率は `View > Zoom` メニューおよびキーボード / マウス / タッチパッドから変更する (詳細は [ズーム](#ズーム))
- スクロール位置は軽さを損なわない範囲でファイル単位で保存する

## Markdown対応

GitHub Flavored Markdown 寄りの表示を基本にする。

- 見出し、段落、強調、打ち消し
- 箇条書き、番号付きリスト、チェックリスト
- コードブロック、インラインコード
- 表
- 引用、水平線
- リンク
- ローカル画像、外部画像
- 自動リンク
- 脚注
- 数式
- Mermaid
- 安全な HTML 埋め込み
- GitHub 形式の Alert (`> [!NOTE]`, `> [!WARNING]` 等)
- 16 進カラーやカラーキーワードのインライン swatch

## コードブロック

- シンタックスハイライトする (`highlight.js` を同梱)
- 長い行は横スクロールせず折り返す
- 等幅フォントを使う
- 言語名を小さく表示する
- 右上にコピーボタンを表示する
- 長いコードでもページ全体の横幅を壊さない

## 表

- 罫線と控えめな背景で読みやすく表示する
- 表だけ横スクロール可能にする
- ページ全体の横スクロールは発生させない

## Mermaid

- fenced code block の `mermaid` を図として描画する
- 描画に失敗した場合は元のコードブロックを表示する
- テーマはアプリのライト / ダークに追従する
- 図は本文幅内に収める
- 大きい図は図エリア内で横スクロール可能にする
- 図のズーム / パン操作は MVP 外

## 数式

- KaTeX で描画する
- インライン数式は `$...$`, `\(...\)` に対応する
- ブロック数式は `$$...$$`, `\[...\]` に対応する
- 描画に失敗した場合は元のテキストを表示する

## HTML埋め込み

- `details`, `summary`, `kbd`, `mark`, `sup`, `sub`, `br`, `span`, `div` など安全な基本 HTML は許可する
- `script`, `iframe`, `object`, `embed`, `style` は除去する
- `onclick` などのイベント属性は除去する
- `javascript:` など危険な URL スキームは除去する
- DOMPurify でサニタイズしてから WebView2 に渡す

## リンク

- ページ内アンカーは同じプレビュー内でスクロールする
- 相対リンク先が Markdown の場合
  - folder mode: SkimDown 内で対象ファイルを開き、ツリー選択も移動する
  - single-file mode: 新しい single-file ウィンドウで対象ファイルを開く
- 外部リンク (`http(s):`) は既定ブラウザで開く (Application 層の `IExternalUriLauncher` 経由)
- 開いたフォルダー外のローカルファイルは原則読み込まない (LinkResolver が `OutOfFolder` として弾く)
- `javascript:` 等の危険スキームはサニタイズ時点で除去する

## 画像

- 開いたフォルダー内のローカル画像は Markdown 本文内で表示する
- 外部画像は読み込みを許可する
- 画像単体をツリー項目としては表示しない
- WebView2 のコンテンツ origin (`https://skimdown-content.example/`) を介して仮想ホストマップされたパスとして渡される ([セキュリティ](#セキュリティ) 参照)

## 本文検索

- `Ctrl + F` で検索バーを表示する
- 表示中 Markdown 内だけを検索する
- 入力に応じて一致箇所をハイライトする
- `Enter` で次へ移動する
- `Shift + Enter` で前へ移動する
- `Esc` で検索バーを閉じる
- 一致件数と現在位置を表示する
- 大文字小文字を区別するかどうかはチェックボックスで切り替える (永続化対象)
- `Edit > Use Selection for Find` (`Ctrl + E`) で現在の選択テキストを検索語にセットする
- ファイル名検索、複数ファイル横断検索は将来拡張

## ズーム

- `View > Zoom > Zoom In` (`Ctrl` + `+`) / `Zoom Out` (`Ctrl` + `-`) / `Actual Size` (`Ctrl` + `0`) で段階変更
- `Ctrl` + マウスホイールでなめらかに変更
- precision-touchpad ピンチでなめらかに変更
- 倍率は **50 % 〜 300 % にクランプ** され、永続化される (`AppSettings.ZoomFactor`)
- 変更は表示中ファイルだけでなく以降のすべてのファイルに適用される

## 変更検知

- 開いているフォルダー配下の Markdown 追加 / 削除 / リネームを `FileSystemWatcher` で検知してツリーを更新する
- 表示中 Markdown が外部更新された場合は自動で再読み込みする
- 表示中 Markdown が削除された場合は空状態に戻す
- Markdown 内参照画像の変更は、次回 Markdown 再描画時に反映する
- single-file mode では tree 再走査は走らず、対象ファイルの content 変更だけが reload を起こす
- 手動 Reload メニューは MVP では不要

## 空状態

- 起動直後またはフォルダー未選択時は、中央にアプリアイコン + `Open Folder...` ボタンを表示する
- フォルダー / `.md` ファイルのドラッグ&ドロップを受け付ける
- Markdown が無いフォルダーでは `No Markdown files found` と `Open Another Folder...` を表示する
- 余計な説明文は置かない

## ウィンドウタイトル

- フォルダー未選択: `SkimDown`
- folder mode: `<フォルダー名> — SkimDown`
- single-file mode: `<ファイル名> — SkimDown`
- folder mode の選択中ファイル名はタイトルに出さない

## メニュー

### File

- `Open Folder…` (`Ctrl + O`)
- `Open Recent` (動的に列挙、最大 16 件、選択で当該フォルダーを開く)
- `Reveal in File Explorer` (現在選択中のファイル、無ければ開いているフォルダー)
- `Copy File Path` (現在選択中のファイル)
- `Close Window` (`Ctrl + W`)

`Save`, `Export`, `Print` は MVP 外。

### Edit

- `Copy` (`Ctrl + C`)
- `Select All` (`Ctrl + A`)
- `Find…` (`Ctrl + F`)
- `Find Next` (`Ctrl + G`)
- `Find Previous` (`Ctrl + Shift + G`)
- `Use Selection for Find` (`Ctrl + E`)

`Cut`, `Paste`, `Undo`, `Redo` は編集しないため設けない (または無効でよい)。

### View

- `Toggle Sidebar` (`Ctrl + B`、single-file mode では無効)
- `Move Sidebar to Right` / `Move Sidebar to Left` (単一項目。現在の位置に応じてラベルが切り替わる。single-file mode では無効)
- `Zoom > Zoom In` (`Ctrl` + `+`)
- `Zoom > Zoom Out` (`Ctrl` + `-`)
- `Zoom > Actual Size` (`Ctrl` + `0`)
- `Theme > System`
- `Theme > Light`
- `Theme > Dark`
- `Theme > <ユーザー登録のカスタムテーマ>` (区切り線の下に動的に列挙)
- `Theme > Open Themes Folder` (Themes フォルダーを Explorer で開く)
- `Theme > Reload Themes` (Themes フォルダーを再走査して一覧を更新)

### Window

- `New Window` (`Ctrl + N`)
- `Minimize` (`Ctrl + M`)
- `Zoom` (ウィンドウ最大化トグル)
- `Bring All to Front`
- 開いている SkimDown ウィンドウ一覧 (動的に列挙、選択でフォーカス)

### Help

- `About SkimDown`

## カラーテーマ

- 組み込みテーマは `System / Light / Dark` の 3 種類
- ユーザーは VS Code 互換のカラーテーマ JSON を Themes フォルダーに置いて追加できる
- Themes フォルダーの場所:
  - **Packaged build (Microsoft Store / sideload MSIX)**: `%LOCALAPPDATA%\Packages\<package-family>\LocalState\Themes\`
  - **Unpackaged build**: `%LOCALAPPDATA%\SkimDownForWindows\Themes\`
- JSON は VS Code の `colors` 辞書のみ参照する。`tokenColors` (シンタックスハイライト) は MVP 外で、コードブロックは `type` (light/dark) に応じて GitHub 風 highlight.js テーマの light / dark を選択する
- 解決済みのテーマ色は CSS 変数 (`--skim-bg` ほか) として WebView2 の HTML に注入する
- 一覧の更新は手動 (`View > Theme > Reload Themes`)。ファイル変更の自動監視は行わない
- 選択中のカスタムテーマが消えた場合は次回起動または Reload 時に `System` にフォールバックする
- 詳細な VS Code キー → SkimDown 変数のマッピング、許容される CSS 値の形式は [ADR-0004](../.github/adr/0004-custom-color-schemes.md) および [README の Custom color schemes](../README.md#custom-color-schemes) を参照

## 永続化項目

設定は `%LOCALAPPDATA%\Packages\<package-family>\LocalState\settings.json` (packaged) または `%LOCALAPPDATA%\SkimDownForWindows\settings.json` (unpackaged) に JSON として保存される。

- 前回開いたフォルダー (`LastFolderPath`)
- 最近開いたフォルダー (`RecentFolders`、最大 16 件、最新が先頭、大文字小文字を区別せず重複排除)
- フォルダーごとの最後に開いた Markdown (`FolderStates[folder].LastSelectedRelativePath`)
- フォルダーごとのツリー開閉状態 (`FolderStates[folder].ExpandedFolders`)
- サイドバー位置 (`SidebarPosition`: Left / Right)
- サイドバー表示状態 (`SidebarVisible`)
- サイドバー幅 (`SidebarWidth`)
- テーマ選択 (`Theme`: System / Light / Dark / Custom)
- カスタムテーマ ID (`CustomThemeId`、`Theme = Custom` の時のみ有効)
- ズーム倍率 (`ZoomFactor`、0.5–3.0 にクランプ)
- 本文検索の Match case (`SearchCaseSensitive`)
- 前回終了時のウィンドウ位置の復元設定 (`RememberWindowPosition`、既定は `false`)
- 前回終了時のウィンドウ位置 (`LastWindowPositionX` / `LastWindowPositionY`)
- 前回終了時のウィンドウサイズ (`LastWindowWidth` / `LastWindowHeight`)

専用 Settings 画面は MVP では作らず、メニュー操作や状態変更を自動保存する。

`RememberWindowPosition = true` のとき、終了時に最後に閉じたウィンドウの位置とサイズを保存し、次回起動時に最初のウィンドウへ適用する。保存された位置・サイズが現在の表示構成で作業領域外になる場合は、最寄りディスプレイの作業領域内にクランプして復元する。

Single-file mode 中は `RecentFolders` / `LastFolderPath` / `FolderStates` / `SidebarVisible` を **一切更新しない**。永続設定は folder mode 用の真実として保持される。

## プロセスモデルと多重起動

- SkimDown for Windows は **ユーザーあたり単一プロセス** として動作する
- `AppInstance.FindOrRegisterForKey("SkimDownForWindowsMain")` で main instance を決め、2 回目以降の起動 (Explorer ダブルクリック / `skimdown` CLI 起動 / file association 経由) は **既存プロセスにアクティベーションが redirect** される
- これにより `settings.json` への並行書き込み競合が原理的に発生しない
- Redirect 受信側は activation 引数 (folder path / file path 群) を解析し、`IWindowService` 経由で必要に応じて新ウィンドウまたは既存空ウィンドウを再利用する
- UI / DI が ready になる前に届いた redirect は pending queue に積み、`OnLaunched` 完了後に drain する
- 設計の詳細は [ADR-0005](../.github/adr/0005-single-file-mode-and-file-activation.md) を参照

## 配布 / ファイル関連付け

- Microsoft Store で SkimDown という名前で配布される (Microsoft Partner Center で予約済み)
- Store 配布版は **Microsoft の署名チェーン** で再署名される (アップロードする `.msixupload` は無署名でよい)
- 同じ MSIX を sideload 用に自己署名した `_sideload.msixbundle` も併せて GitHub Releases で配布する
- Identity: `45014okazuki.SkimDown` / Publisher: `CN=57A8C5FA-395A-4109-91A0-CF1B93556B5D` / PublisherDisplayName: `okazuki`
- `Package.appxmanifest` で次を宣言する:
  - **File type association** (`windows.fileTypeAssociation`): `.md`, `.markdown` を `Markdown Document` として SkimDown に関連付け、`OpenIsSafe="true"` を付ける
  - **App execution alias** (`windows.appExecutionAlias`): `skimdown.exe` を `skimdown` として登録 (任意の作業ディレクトリから `skimdown` コマンドで起動できる)

## セキュリティ

- 通常配布の Win32 / MSIX アプリとして提供する (Microsoft Store では `runFullTrust` の Win32 アプリ扱い)
- フォルダー / ファイルアクセスはユーザーが選択したフォルダー、ドロップしたフォルダー / ファイル、CLI 引数 / file association で渡されたパス、および永続化された RecentFolders / LastFolderPath に限る
- 読み取り専用で、開いた Markdown ファイルへの書き込み権限は要求しない
- WebView2 内では **Markdown 本文中の任意 JavaScript は実行しない**
- HTML は DOMPurify でサニタイズしてから描画する
- WebView2 の **2-origin 分離** を採用する:
  - レンダラーアセット (HTML / CSS / JS / フォント) は `https://skimdown-app.example/` 配下の virtual host から配信
  - ユーザーが開いたフォルダーは別 origin `https://skimdown-content.example/` にマップする
  - これにより renderer 側のバグが起きても content origin (ユーザーフォルダー) を直接読み出すことはできない
- Markdown 本文の受け渡しは **`CoreWebView2.PostWebMessageAsJson` のみ** を使う。`NavigateToString` は二重 origin 分離を破壊するため使わない
- 外部リンク (`http(s):`) は WebView2 内で開かず、`IExternalUriLauncher` (`Windows.System.Launcher` 実装) 経由で既定ブラウザに渡す
- アプリから AI サービスや外部 API には通信しない (ローカル / 外部画像と外部リンク起動を除く)

## エラー表示

- Markdown が UTF-8 として読めない場合は短いエラーを表示する
- UTF-8 BOM ありは許可する
- 文字コード自動判定、Shift_JIS / CP932 対応は MVP 外
- Mermaid や数式の描画失敗は、可能な限り元テキスト表示へフォールバックする
- 未捕捉例外はユーザー LocalAppData 下のクラッシュログ (`SkimDownForWindows-crash.log` または `IAppLogger` の出力先) に追記される

## テスト方針

MVP では純粋ロジックをユニットテストで固める。テストプロジェクト `SkimDownForWindows.Tests` は `net10.0` (プラットフォーム中立) で構成し、`SkimDownForWindows.Domain` と `SkimDownForWindows.Application` のみを参照する (`Infrastructure` / WinUI 3 への依存は持たない)。

- Markdown ファイル走査 (`MarkdownScannerTests`)
- 除外ディレクトリ / 隠しファイル判定
- ツリー構築と並び順 (`MarkdownTreeBuilderTests`)
- 初期選択ロジック (`InitialSelectionPickerTests`)
- 相対リンク解決とフォルダー外参照の拒否 (`LinkResolverTests`)
- 設定保存のデフォルト値・正規化 (`AppSettingsTests`)
- single-file mode で RecentFolders / LastFolderPath / FolderStates / SidebarVisible が更新されないこと (`MainPageViewModelSingleFileTests`)
- `CommandLineLauncher.TryResolveActivation` / `Classify` による activation 分類

UI 自動テストは MVP 外。手動確認手順は README または `docs/` に記載する。

上流 [`07JP27/SkimDown/samples/`](https://github.com/07JP27/SkimDown/tree/main/samples) を使う統合テストはオプトインで `SKIM_SAMPLES_PATH` 環境変数で有効化する (README の Development 節を参照)。

## 上流仕様との対応

- 上流 macOS 版で `Cmd` キーで割り当てられているショートカットは、Windows 版ではすべて `Ctrl` に置き換える
- `Reveal in Finder` は `Reveal in File Explorer` に名称変更
- `security-scoped bookmark` (macOS の sandbox 用 API) は Windows では不要。`RecentFolders` は単純な絶対パス配列として保存する
- `~/Library/Application Support/SkimDown/` 相当の場所は `%LOCALAPPDATA%\Packages\<package-family>\LocalState\` (packaged) または `%LOCALAPPDATA%\SkimDownForWindows\` (unpackaged) になる
- `Hardened Runtime` / `notarization` (macOS 配布の概念) は適用されない。代わりに **Microsoft Store の署名チェーン** または **自己署名 + Trusted People** によって配布検証される
- macOS の Dock メニューはなく、Windows のタスクバージャンプリストや右クリック起動は MVP 外
- 上流の `WKWebView` は WinUI 3 の `WebView2` (Microsoft Edge / Chromium ベース) で置き換える。挙動的に互換になるよう努めるが、フォントメトリクスや一部のシステムフォント差異 (`SF Pro` vs `Segoe UI`) は許容する
