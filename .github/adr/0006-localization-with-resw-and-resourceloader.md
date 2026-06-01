# 0006. UI 文字列ローカライズに MRT (resw) と ResourceLoader を採用する

- 日付: 2026-06-01
- ステータス: Proposed
- 関連 ADR: [0002](0002-clean-architecture-layered-projects.md)

## コンテキスト

SkimDown for Windows は、初期は UI 文字列を XAML / コードビハインドに直接ハードコードしていた (英語のみ)。これは次の問題を生じていた。

- UI 文字列が複数ファイルに散らばっており、変更時に全置換が必要 (typo / アクセシビリティ文言改善のたびに XAML と code-behind の両方を編集)
- スクリーンリーダー向けの `AutomationProperties.Name` や `ToolTipService.ToolTip` も同じ場所にハードコードされていて、a11y 改善とリリース時に翻訳影響範囲を取れない
- 将来別言語 (例: `ja-JP`) を追加したくなった時に、resw → satellite assemblies のような移行コストが大きい
- アプリ名 / ウィンドウタイトルが `MainWindow.xaml.cs` ctor で文字列リテラル

これらを解決しつつ、ADR-0002 で確立した Clean Architecture (Application / Infrastructure / Presentation の依存方向) を壊さないローカライズ機構が必要だった。

実装方針を決めるうえで、本リポジトリ固有の制約があった。

- **Application プロジェクトは `net10.0` (プラットフォーム中立)** で、`Windows.ApplicationModel.Resources` のような WinRT 型を `using` できない
- **Infrastructure プロジェクトは `Microsoft.WindowsAppSDK` を参照しない** (ADR-0002) ため、リソースローダーを Infrastructure に置く選択肢は取れない
- アプリは WinUI 3 + Windows App SDK 2.0.1 + MSIX (Microsoft Store / sideload) で配布するため、**MRT (Modern Resource Technology) と PRI (Package Resource Index) が既定で使える**
- ViewModel (`MainPageViewModel`) が UI 文字列を直接保持していない (Format パラメーターのみ受け取り、Page 側で `string.Format(_strings.GetString(...), value)`) — このパターンが既に確立している
- 現状ロケールは英語 (`en-US`) のみで、近い将来別言語を追加する具体的予定は無いが、いつでも追加できる土台は欲しい

## 決定

### 1. ローカライズ機構

**Modern Resource Technology (MRT) + resw + `Windows.ApplicationModel.Resources.ResourceLoader`** を採用する。

- リソースは `src/SkimDownForWindows/Strings/<locale>/Resources.resw` に置く (現状 `en-US` のみ)
- `Package.appxmanifest` は `<Resource Language="x-generate" />` を維持し、サポートロケールは `Strings/<locale>/` フォルダー一覧から MSIX ビルド時に自動生成する
- XAML 内の文字列は **`x:Uid` バインド** を第一選択にする (例: `<MenuFlyoutItem x:Uid="OpenFolderMenuItem" />` → `OpenFolderMenuItem.Text` を resw から自動解決)
- XAML で表現できない動的文字列 (count フォーマット / 状態遷移ラベル / Recent Folders 動的メニュー / DragDrop オーバーレイ等) は **コードビハインドから `ResourceLoader.GetForViewIndependentUse().GetString("Foo/Bar")`** で取得する

### 2. レイヤー境界

| 層 | ローカライズ関連で許可される型 |
|---|---|
| Domain | (使用しない。値オブジェクトに UI 文字列は持たない) |
| Application | (使用しない。`Windows.ApplicationModel.Resources` は WinRT 型で `net10.0` から `using` 不可) |
| Infrastructure | (使用しない。`Microsoft.WindowsAppSDK` 不参照ポリシーと整合) |
| Presentation (App) | `Windows.ApplicationModel.Resources.ResourceLoader` / `x:Uid` / `Resources.resw` |

Application 層から「ローカライズされた文字列」を返す必要が出てきた場合の対応は、**当面は新ルールで対処しない**。具体的には:

- 数値や日付のフォーマットは UI 側 (Page / `x:Bind` のコンバーター) で行う
- ViewModel は UI 文字列を保持せず、フォーマット用の値 (`int MarkdownCount` など) のみ公開する
- 例外メッセージはユーザー向け文字列にしない (現状 `IAppLogger` に流すのみ)

将来 ViewModel が複数バリアントの文字列を返す必要が出た時は、`Application.Abstractions/ILocalizedStrings` のような抽象を追加する別の ADR を起こす (今は YAGNI)。

### 3. resw のキー命名規約

resw 上のキー (`name` 属性) は次の 2 系統が混在する。

| 用途 | resw キー形式 | 例 | 消費側 |
|---|---|---|---|
| `x:Uid` バインド | `<UidName>.<PropertyName>` | `AboutDialog.Title`, `OpenFolderMenuItem.Text`, `SearchPreviousButton.AutomationProperties.Name` | XAML (`x:Uid="AboutDialog"` で自動解決) |
| `ResourceLoader.GetString` 直呼び | `<Category>.<Key>` | `MarkdownCount.OneFile`, `Sidebar.MoveToRight`, `RecentFolders.Empty` | code-behind (`_strings.GetString("MarkdownCount/OneFile")`) |

両者は resw 上では同じ `<data name="...">` だが、`ResourceLoader.GetString` は **resource map path 表記**のため `.` を `/` に置き換えて指定する。これは MRT の仕様であり、変えられない。

### 4. ResourceLoader 取得方法

`MainWindow` / `MainPage` のコードビハインドで:

```csharp
private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse();
```

を `private readonly` フィールドとして 1 つ保持する。`GetForViewIndependentUse()` は WinUI 3 では既定の app resource map (`Strings/...`) を返す。

ウィンドウ / ページごとに 1 インスタンス持っても MRT の内部キャッシュにより実害は無いが、複数 `GetForViewIndependentUse()` 呼び出しを 1 ファイル内で散らさない。

## 結果（Consequences）

### ポジティブ

- UI 文字列が `Resources.resw` 1 ファイルに集約され、a11y 文言や label を変更する PR の影響範囲が「resw 差分 + 必要なら code-behind」で完結する
- 将来別ロケール (例: `ja-JP`) を追加する時の手順が「`Strings/ja-JP/Resources.resw` を作って翻訳した `<data>` を入れるだけ」になり、コード変更ゼロ
- a11y (`AutomationProperties.Name`) / アクセラレータヒント (`ToolTipService.ToolTip`) も resw に並ぶため、スクリーンリーダー向け文言が単一ソースで管理される
- `x:Uid` ベースなので `MainPage.xaml` 等の文字列が宣言的に解決され、デザイナー / ホットリロード時の整合性が取りやすい
- Clean Architecture (ADR-0002) の依存方向を壊さない: `Windows.ApplicationModel.Resources` は Presentation 専有

### ネガティブ

- resw キーが 2 種類のキー形式 (`x:Uid` バインド形式 / `ResourceLoader` 直呼び形式) で混在し、命名規約を意識する必要がある
- `ResourceLoader.GetString` の path 表記 (`.` → `/` 置換) が WinUI 初学者には直観的でない (resw 上は `Foo.Bar`、code は `"Foo/Bar"`)
- ViewModel が UI 文字列を持てないため、count フォーマットなど一部のテキスト構築ロジックは Page 側に残る (`MarkdownCount.OneFile` / `ManyFiles` の分岐は `MainPage.xaml.cs` 内)
- 単体テスト (`SkimDownForWindows.Tests` は `net10.0`) からは `ResourceLoader` をテストできない (テスト対象から外す)
- 将来複数ロケールを追加した場合の翻訳プロセス (誰が翻訳するか、レビューフローはどうするか) はこの ADR では決めない (運用が始まる時に別 ADR で扱う)

### ニュートラル

- `Package.appxmanifest` の `<Resource Language="x-generate" />` は変更しない (Single-language MSIX 時代の `en-US` 固定では無く、MRT の既定動作で `Strings/<locale>/` から自動展開する)
- `<DefaultLanguage>` を `Package.appxmanifest` に明示する選択肢もあるが、現状の MRT 既定挙動 (フォールバック = `en-US`) で問題が無いため指定しない
- アプリ名 / Description (`Package.appxmanifest` 内のメタデータ) のローカライズは別問題 (`ms-resource:` 参照 + PRI 化) で、本 ADR の対象外。現状ハードコード文字列のままで、必要になった時に別途対応する

## 検討した代替案

### 代替案 A: 文字列をハードコードのまま維持

- 概要: `MainPage.xaml` / コードビハインドに英語文字列リテラルを直接書き続ける
- 採用しなかった理由: a11y 改善や label 変更のたびに XAML と code-behind の両方を編集する必要がある。将来別ロケールを追加するときの移行コストが大きい。なにより `AutomationProperties.Name` / `ToolTipService.ToolTip` などのアクセシビリティ文言が "本文" の翻訳と一緒に管理されないため、a11y 監査時に翻訳もれ・更新もれが起きやすい

### 代替案 B: `.resx` + `Resources.Designer.cs` + `System.Resources.ResourceManager`

- 概要: 古典的な .NET ローカライズ機構。`Resources.resx` から `Resources.Designer.cs` を自動生成し、`Resources.MyKey` のような静的プロパティでアクセスする
- 採用しなかった理由: WinUI 3 では `x:Uid` バインドが MRT の `.resw` と統合されており、`.resx` + Designer プロパティでは XAML 側からの宣言的バインドが効かない (code-behind で `MyControl.Text = Resources.MyKey` を毎回書く必要がある)。MSIX 配布で MRT が既定の選択肢になっているのに、わざわざ古典機構を併用する利点が無い

### 代替案 C: `IStringLocalizer<T>` 抽象を Application 層に立てる (ASP.NET Core 流)

- 概要: Application 層に `IStringLocalizer<T>` 抽象を定義し、Infrastructure (または Presentation) で `ResourceLoader` ラッパー実装を提供する。ViewModel が文字列キーを受け取って解決する
- 採用しなかった理由: 現状 ViewModel は UI 文字列を保持していない (フォーマットは Page で行う) ため、抽象を立てる動機が無い。MRT は WinRT 型のため Infrastructure (`Microsoft.WindowsAppSDK` 不参照) には実装を置けず、Presentation で `ILocalizedStrings` を実装することになるが、それなら Page から直接 `ResourceLoader` を呼ぶのと等価で、ただ間接層が増えるだけ。Clean Architecture を盾にした過剰な抽象化になる (YAGNI)。将来 ViewModel がローカライズ文字列を返す要件が出た時に、別 ADR で追加する

### 代替案 D: コミュニティライブラリ (例: `Toolkit.Localization`, `WinUI3Localizer`)

- 概要: NuGet で配布されている WinUI 向けローカライズライブラリを採用する
- 採用しなかった理由: MRT / `ResourceLoader` は Windows App SDK に最初から入っており、追加依存ゼロで a11y 連動 (`AutomationProperties.Name` 等) が効く。サードパーティライブラリを入れると、保守者の依存追跡コスト・脆弱性対応コスト・MSIX サイズが増える。本アプリのローカライズ要件は単純 (文字列差し替え + 動的フォーマット) で、外部ライブラリを正当化するほどの複雑性が無い

### 代替案 E: アプリを完全に英語固定 (`<Resource Language="en-US" />`) にして resw も持たない

- 概要: 国際化を将来も諦め、ハードコード英語のままにする
- 採用しなかった理由: a11y 文言 (スクリーンリーダー向け `AutomationProperties.Name`) を一元管理したい動機は国際化と独立に成立する。resw に集約すれば、英語 1 ロケールでも翻訳一覧が 1 ファイルになり、レビュー / 文言改善 / typo 修正が容易になる

## 参考リンク

- ADR-0002 クリーンアーキテクチャー風のプロジェクト分割と DI: [0002-clean-architecture-layered-projects.md](0002-clean-architecture-layered-projects.md)
- Windows App SDK MRT / Resource management: <https://learn.microsoft.com/windows/apps/windows-app-sdk/mrtcore/mrtcore-overview>
- `Windows.ApplicationModel.Resources.ResourceLoader`: <https://learn.microsoft.com/uwp/api/windows.applicationmodel.resources.resourceloader>
- `x:Uid` directive (XAML): <https://learn.microsoft.com/windows/apps/design/globalizing/use-uid-attribute>
- 現状スナップショット: [`docs/localization.md`](../../docs/localization.md)
