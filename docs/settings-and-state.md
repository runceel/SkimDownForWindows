# 設定 / 状態モデル

SkimDown for Windows は、グローバル設定 (テーマ・サイドバー幅・ズーム…) と **フォルダーごとの状態** (前回選択ファイル・展開中フォルダー) を 1 つの `settings.json` に保存する。書き込みは `SemaphoreSlim` で single-flight、ファイル更新は tmp + atomic move。Single-file mode は永続キーを意図的に書き換えない。

## 構造

### [`AppSettings`](../src/SkimDownForWindows.Application/Models/AppSettings.cs) — グローバル設定

| プロパティ | 型 | 既定値 | 永続化単位 |
|---|---|---|---|
| `Theme` | `AppTheme` | `System` | グローバル |
| `CustomThemeId` | `string?` | `null` | `Theme=Custom` の時のみ意味を持つ。それ以外は `null` 強制 |
| `ZoomFactor` | `double` | `1.0` | グローバル。範囲 0.5–3.0 にクランプ |
| `SearchCaseSensitive` | `bool` | `false` | グローバル |
| `SidebarWidth` | `double` | `280` | グローバル |
| `SidebarVisible` | `bool` | `true` | グローバル (folder mode 用) |
| `SidebarPosition` | `SidebarPosition` | `Left` | グローバル |
| `ContentMaxWidth` | `ContentMaxWidth` | `Full` | グローバル。Markdown プレビュー本文の最大幅段階 (Standard=760px / Wide=960px / ExtraWide=1200px / Full=無制限)。CSS の `max-width` として効くため、ウィンドウが指定段階より狭ければ本文はウィンドウ幅にフィットし、広ければ指定段階で頭打ちになる |
| `OpenContainingFolderOnSingleFileActivation` | `bool` | `false` | グローバル。`OpenSingleFileActivation` を軽量 single-file mode で開く (`false`) か、親フォルダーを開いて対象ファイルを選択する (`true`) かを切り替える |
| `RecentFolders` | `List<string>` | `[]` | 最新が先頭。最大 `MaxRecentFolders = 16` 件 |
| `LastFolderPath` | `string?` | `null` | 起動時の復元キー |
| `FolderStates` | `Dictionary<string, FolderState>` | `{}` | キーは正規化済みフォルダー絶対パス |

### [`FolderState`](../src/SkimDownForWindows.Application/Models/AppSettings.cs) — フォルダー固有状態

| プロパティ | 型 | 既定値 | 説明 |
|---|---|---|---|
| `LastSelectedRelativePath` | `string?` | `null` | フォルダー基準の forward-slash 相対パス |
| `ExpandedFolders` | `List<string>` | `[]` | 展開中フォルダーの forward-slash 相対パス |

## 不変条件と正規化

[`AppSettings.NormalizeAfterLoad`](../src/SkimDownForWindows.Application/Models/AppSettings.cs) はロード直後に呼ばれ、次の不整合を直す。書き込み時はこの不変条件を保つ責務がアプリ側にある。

| 元の状態 | 正規化後 |
|---|---|
| `Theme=Custom` かつ `CustomThemeId` が空 | `Theme=System`、`CustomThemeId=null` |
| `Theme != Custom` かつ `CustomThemeId` に値あり | `CustomThemeId=null` (書き出し時に省略) |

カスタムテーマ ID が現在のテーマレジストリに存在しないケースは [`ColorSchemeRegistry.Normalize`](../src/SkimDownForWindows.Application/Theme/ColorSchemeRegistry.cs) で再度 `System` に戻される (`AppSettings` レイヤでは「ID が空文字でないか」しか見ない)。

[`AppSettings.UpdateRecentFolders(folderPath)`](../src/SkimDownForWindows.Application/Models/AppSettings.cs) は次を行う:

1. 大文字小文字を区別せず既存エントリを削除
2. 先頭に `folderPath` を挿入
3. 16 件超なら末尾を切り捨て
4. `LastFolderPath` を `folderPath` に同期

## 保存先 ([`SettingsFolderProvider`](../src/SkimDownForWindows.Infrastructure/IO/SettingsFolderProvider.cs))

実行形態によって基底フォルダーが変わる:

| 実行形態 | 基底フォルダー |
|---|---|
| Packaged (MSIX install / Microsoft Store / sideload) | `Windows.Storage.ApplicationData.Current.LocalFolder.Path` |
| Unpackaged (`dotnet run` / 開発時の生 exe 実行) | `%LOCALAPPDATA%\SkimDownForWindows` |

| ファイル / フォルダー | 場所 | 用途 |
|---|---|---|
| `settings.json` | 基底フォルダー直下 | `AppSettings` の永続化先 |
| `Themes/*.json` | 基底フォルダー配下 `Themes/` | ユーザー登録カスタムカラーテーマ ([theming.md](theming.md) 参照) |
| `SkimDownForWindows-*.log` | `%LOCALAPPDATA%` (パッケージ外固定) | `IAppLogger` / クラッシュフォールバック |

判別は `try { Windows.Storage.ApplicationData.Current.LocalFolder.Path } catch { LOCALAPPDATA }` の例外フォールバック。パッケージ識別子があれば WinRT が成功し、無ければ `%LOCALAPPDATA%` を使う。

## ファイル書き込みモデル ([`JsonSettingsRepository`](../src/SkimDownForWindows.Infrastructure/IO/JsonSettingsRepository.cs))

ファイル破損リスクを下げるため、書き込みは次の手順:

1. `SemaphoreSlim` (`_saveGate`, 1 並列) を取得 — single-flight
2. `JsonSerializer.Serialize(_current)` でメモリ上に JSON 生成
3. `settings.json.tmp` に全文書き出し
4. `File.Move(tmp, settings.json, overwrite: true)` で **atomic** に差し替え
5. semaphore release

`SaveAsync` / `FlushSync` の 2 系統:

| API | 用途 | 呼ばれる場所 |
|---|---|---|
| `Task SaveAsync()` | 通常の永続化 (VM の変更ごと) | `MainPageViewModel.OpenFolderAsync` / `SelectAndLoadAsync` / `MainPage` のメニューハンドラ各種 |
| `void FlushSync()` | アプリ終了時のドレイン | [`App.ExitApp`](../src/SkimDownForWindows/App.xaml.cs) (`onLastWindowClosed`) |

ロード ([`Load`](../src/SkimDownForWindows.Infrastructure/IO/JsonSettingsRepository.cs)) はアプリ起動直後の `App.OnLaunched` で 1 回だけ呼ばれる。JSON 破損時はデフォルト `AppSettings` を保持し、例外は出さない。

## 単一インスタンスとの整合

「2 つのプロセスが同時に `settings.json` を更新する」競合は、Process レベルで `AppInstance.FindOrRegisterForKey` の **single-instance redirect** によって排除される ([activation-and-single-instance.md](activation-and-single-instance.md) 参照)。プロセス内部の複数ウィンドウからの並列書き込みは `SemaphoreSlim` で序列化される。結果として、ある瞬間に `settings.json` を書いているのは常に 1 つの fiber だけ。

## JSON フォーマット

シリアライザ設定 ([`CreateJsonOptions`](../src/SkimDownForWindows.Infrastructure/IO/JsonSettingsRepository.cs)):

- `WriteIndented = true` (人間が diff を読む前提)
- `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` (`CustomThemeId=null` 等を省く)
- `AppTheme` は [`AppThemeJsonConverter`](../src/SkimDownForWindows.Application/Models/AppThemeJsonConverter.cs) で文字列化 (`"system"|"light"|"dark"|"custom"`)。読み込みは旧フォーマットの整数 `0..3` も寛容に受理

サンプル:

```json
{
  "theme": "custom",
  "customThemeId": "my-dark",
  "zoomFactor": 1.25,
  "openContainingFolderOnSingleFileActivation": false,
  "sidebarWidth": 320,
  "sidebarVisible": true,
  "sidebarPosition": "Left",
  "recentFolders": [ "C:\\src\\notes", "D:\\docs" ],
  "lastFolderPath": "C:\\src\\notes",
  "folderStates": {
    "C:\\src\\notes": {
      "lastSelectedRelativePath": "subdir/README.md",
      "expandedFolders": [ "subdir" ]
    }
  }
}
```

## Single-file activation の 2 モード

`OpenContainingFolderOnSingleFileActivation` により、`.md` 起動の扱いは次の 2 つに分岐する。

| 設定値 | 挙動 |
|---|---|
| `false` (既定) | 従来どおり single-file mode (`RootItems` は空、サイドバーは強制非表示、対象ファイルだけ読む軽量パス) |
| `true` | 対象 Markdown の親フォルダーを folder mode で開き、対象ファイルを選択して表示する。初期表示ではサイドバーを一時的に折り畳むが、永続 `SidebarVisible` は書き換えない |

## single-file mode (`OpenContainingFolderOnSingleFileActivation=false`) で更新しないキー

Explorer から `.md` ダブルクリック / `skimdown README.md` 等で起動した single-file mode のウィンドウは、**永続化キーを一切書き換えない**。これにより:

- folder mode の `RecentFolders` 履歴が Markdown のクイック閲覧で汚されない
- `LastFolderPath` が壊れず、次回通常起動で前回フォルダーが正しく復元される
- `FolderStates` の per-folder 状態を温存できる
- `SidebarVisible` が folder mode 用の真実として保たれ、File > Open Folder で folder mode に戻った時にサイドバーが想定通りに復元される

具体的に Single-file mode で **更新しない** キー:

| キー | 更新しない理由 |
|---|---|
| `RecentFolders` | Markdown のクイック閲覧でフォルダー履歴を汚さない |
| `LastFolderPath` | 次回起動時の復元先を保持 |
| `FolderStates` (`LastSelectedRelativePath` / `ExpandedFolders`) | folder mode 専用 |
| `SidebarVisible` | folder mode 用の真実 |

「更新する」キー (single-file mode 中もグローバル設定として保存される):

| キー | 備考 |
|---|---|
| `Theme` / `CustomThemeId` | View > Theme でテーマ変更すれば反映 |
| `ZoomFactor` | Ctrl+wheel / `View > Zoom` で変えると保存される |
| `SearchCaseSensitive` | Find バーで切り替えると保存される |
| `SidebarWidth` / `SidebarPosition` | 仕様上更新されうるが single-file mode ではサイドバー非表示のため通常は変化しない |

## 関連

- ADR: [0005 Single-file mode と File Activation の導入](../.github/adr/0005-single-file-mode-and-file-activation.md) (single-file mode で永続キーを触らない理由)
- SPEC: [`design/SPEC.md`](../design/SPEC.md) の「フォルダを開く」「初期選択」「ツリー」「ズーム」「単一ファイルを開く」
- 隣接ドキュメント: [`theming.md`](theming.md), [`activation-and-single-instance.md`](activation-and-single-instance.md), [`markdown-content-pipeline.md`](markdown-content-pipeline.md)
- コード: [`AppSettings.cs`](../src/SkimDownForWindows.Application/Models/AppSettings.cs), [`JsonSettingsRepository.cs`](../src/SkimDownForWindows.Infrastructure/IO/JsonSettingsRepository.cs), [`SettingsFolderProvider.cs`](../src/SkimDownForWindows.Infrastructure/IO/SettingsFolderProvider.cs), [`AppThemeJsonConverter.cs`](../src/SkimDownForWindows.Application/Models/AppThemeJsonConverter.cs)
