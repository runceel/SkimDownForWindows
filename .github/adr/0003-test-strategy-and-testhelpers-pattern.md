# 0003. Application / Domain 単体テストの戦略と TestHelpers パターン

- 日付: 2026-05-24
- ステータス: Proposed
- 関連 ADR: [0002](0002-clean-architecture-layered-projects.md)

## コンテキスト

[ADR-0002](0002-clean-architecture-layered-projects.md) でクリーンアーキテクチャー風の層分割と DI を導入した際、テストプロジェクトについて以下のみが決まっていた。

- `SkimDownForWindows.Tests` は `net10.0` (プラットフォーム中立)
- `SkimDownForWindows.Domain` と `SkimDownForWindows.Application` のみ `ProjectReference` する
- `SkimDownForWindows.Infrastructure` (windows TFM) は参照しない
- `IFileSystem` を必要とするテストでは `TestHelpers/RealFileSystem` を提供する

決まっていなかった項目:

- **テストダブルの作り方**: モックライブラリ (Moq / NSubstitute / FakeItEasy) を採用するのか、手書きの in-memory 実装を置くのか
- **手書きにする場合の置き場所と命名規約**
- **`async void` ハンドラーや fire-and-forget の完了同期方法**
- **純粋サービス (`MarkdownScanner` 等) はテスト内で実体を使うか、それも差し替えるか**
- **「仕様要件のテスト」と「現挙動の golden test」をどう区別するか**

[ADR-0002](0002-clean-architecture-layered-projects.md) 後に最初の大きなテスト追加 (Application 層の単体テスト網羅性向上、100 件達成) を進める中で、これらの方針を一気に固める必要が出てきた。決定を文書化しないと、次に AI / 新規参画者がテストを書くたびにスタイルが分散し、`async void` の完了待ちを各テストで再実装するなど一貫性が崩れる。

### 観察された具体的な制約

- `ISettingsRepository.SaveAsync` を呼ぶ `async void OnTreeMayHaveChanged` と `_ = SelectAndLoadAsync(...)` が `MainPageViewModel` 内に存在し、テスト側から `await` できない
- `IFolderWatcher` はイベント (`TreeMayHaveChanged`, `FileContentChanged`) を発火する I/F であり、テストから外部発火させたい
- 純粋サービス (`MarkdownScanner` / `MarkdownTreeBuilder` / `InitialSelectionPicker` / `LinkResolver`) は実体で使ったほうが統合境界が自然で、内部実装をモック化すると逆にテストが脆くなる
- `MarkdownScanner` は実ファイルシステムを `IFileSystem` 経由で叩くため、テストは一時ディレクトリを使う必要がある (MSTest `MethodLevel` parallelize と衝突しない設計が要る)

## 決定

**Application / Domain 単体テストの戦略として次のポリシーを採用する。**

### 1. テストダブルは手書き、`TestHelpers/` 配下に置く

- モックライブラリ (Moq / NSubstitute / FakeItEasy) は採用しない
- Application 層の各 `I<X>` 抽象に対する in-memory 実装を `src/SkimDownForWindows.Tests/TestHelpers/` に置く
- ファイル単位で 1 抽象 = 1 ダブル

### 2. ダブルの命名規約

意図に応じて prefix を使い分ける。

| Prefix | 用途 | 例 |
|---|---|---|
| `Stub<X>` | 入力に対して固定値を返すだけ | `StubMarkdownFileReader`, `StubSystemThemeProvider` |
| `Fake<X>` | 内部状態を持ち、テストから状態を進めたり外部発火させたりできる | `FakeFolderWatcher` (イベント発火 API 付き) |
| `Recording<X>` | 副作用を持つ I/F 呼び出しを記録するだけ | `RecordingShellService`, `RecordingClipboardService` |
| `InMemory<X>` | 永続化や読み書きを伴う I/F のメモリ実装 | `InMemorySettingsRepository` |
| `Real<X>` | 実体に近い実装をテスト用にラップしたもの (Infrastructure 不参照のための代替) | `RealFileSystem` |

### 3. 純粋サービスは実体を使う (ハイブリッド方針)

- `MarkdownScanner`, `MarkdownTreeBuilder`, `InitialSelectionPicker`, `LinkResolver` は **テスト内で `new` する**
- これらは副作用ゼロ、または `IFileSystem` 抽象のみに依存しており、ダブル化する利点がない
- `MainPageViewModel` のテストではこれら純粋サービス実体 + I/O 抽象ダブル + `RealFileSystem` + 一時ディレクトリの組み合わせで構築する

### 4. `async void` / fire-and-forget の完了同期

- **`Thread.Sleep` / `Task.Delay` ベースのポーリングは禁止** (flaky テストの温床)
- 副作用を観測できるダブル側に `WaitForXxxAsync(int expected, TimeSpan? timeout)` を実装する
  - 例: `InMemorySettingsRepository.WaitForSaveCountAsync(2)` は `SaveAsync` が 2 回以上呼ばれるまで待つ
  - 実装は `TaskCompletionSource` のリストを内部に持ち、副作用呼び出し時に `TrySetResult` する
  - timeout 超過は `TimeoutException` で fail させ、テスト名と実呼び出し回数を含める

### 5. 一時ディレクトリは固有化

- 実ファイルシステムが必要なテストは `Path.Combine(Path.GetTempPath(), "skim-xxx-" + Path.GetRandomFileName())` で固有のディレクトリを作る
- `[TestInitialize]` で作成、`[TestCleanup]` で `Directory.Delete(recursive: true)`
- MSTest の `MethodLevel` parallelize (`assembly:Parallelize`) と衝突しない

### 6. 「仕様要件のテスト」と「現挙動の golden test」を区別

- 仕様要件 (SPEC で要求されている動作): 通常のテスト名 `Action_State_ExpectedOutcome`
- 現挙動の固定化 (バグ寄りだが現状そう動く / 意図的だが仕様には書かれていない): テスト名に `_CurrentBehavior` / `_Documents...Behavior` 等を含め、コメントで明示
- 仕様変更を伴う修正は別 PR + 必要に応じて新 ADR で扱う

### 7. テスト対象外 (現フェーズ)

- Infrastructure 層の I/O ラッパー (`LocalFileSystem`, `LocalMarkdownFileReader`, `JsonSettingsRepository`, `FileSystemFolderWatcher`, `ExplorerShellService` 等) は単体テスト対象外
  - Tests プロジェクトが `net10.0` で Infrastructure (`net10.0-windows*`) を参照しないため
  - 現状は手動 / smoke で検証
- Presentation 層 (`MainWindow`, `MainPage`, `MarkdownPreview`) は単体テスト対象外
  - WinUI 3 が UI スレッド前提のため `net10.0` テストでは扱えない
- Domain 層の純粋 enum / record (`AppTheme`, `SidebarPosition`, `LinkKind`, `LinkClassification`) は直接の単体テスト対象外
  - 値そのもののテストは過剰。JSON ラウンドトリップ等で間接的に保持を確認する

## 結果（Consequences）

### ポジティブ

- 新規テスト追加時に、どこにダブルを置くか・どう命名するか・どう同期するかが**事前に決まっている**ため、AI / 新規参画者が再発明しなくて済む
- I/F 変更が手書きダブルの**コンパイルエラーとして検出される** (Expression ベースのモックライブラリだと実行時まで失敗が見えない)
- 動的プロキシライブラリ (Castle.DynamicProxy 等) への依存ゼロ → trim / AOT 親和性を維持
- `WaitForSaveCountAsync` のような sync helper を再利用することで、`async void` ハンドラーのテストが Sleep / Delay 不要で書ける
- `TestHelpers/` を見れば**現在テスト可能な抽象が一覧できる**

### ネガティブ

- 抽象が増えるたびに手書きダブルを 1 ファイル追加するコストが発生する (現状 10 抽象なので低コスト)
- 「verify が複雑な呼び出しパターン」を検証したい時 (例: `M(x.Bar == 42) を 3 回呼んだ`) は手書きで if 文を書く必要がある (現状そのようなテストは無い)
- Infrastructure / Presentation のカバレッジゼロが続くため、I/O ラッパーの回帰検出は手動 / smoke 依存

### ニュートラル

- `TestHelpers/RealFileSystem` は本物の `LocalFileSystem` と挙動を揃える責務を負う (差分が出ると Tests が偽陽性 / 偽陰性を出す)
- 一時ディレクトリの cleanup は best-effort (Windows のロックで失敗することがあるが、`GetRandomFileName` で衝突しないため許容)

## 検討した代替案

### 代替案 A: モックライブラリを採用する (Moq / NSubstitute / FakeItEasy)

- 概要: 手書きダブルの代わりに `.Setup()` / `.Verify()` 構文でテストごとに振る舞いを記述
- 採用しなかった理由:
  - 抽象が少なく (10 個) かつ各 I/F が小さい (1〜3 メソッド) ため、書く一回コストが低く再利用が効く
  - **`IFolderWatcher` のイベント発火** と **`ISettingsRepository.SaveAsync` の完了待ち** はモックライブラリでも結局 `Setup` + `Callback` + `TaskCompletionSource` を自前で書く羽目になる (実質 fake と同じ)
  - Expression ベースの `It.Is<T>(...)` は I/F リファクタ時に**実行時まで失敗が表面化しない**
  - Castle.DynamicProxy 経由の動的プロキシは trim 警告 / AOT 不適合の原因となり得る (Infrastructure / App は trim 候補)
  - copilot-instructions の精神 (「`new` を抑制」「static を避ける」「依存を絞る」) と整合させたい
- **モックライブラリが勝つ典型ケース** (このリポジトリには該当しない):
  - 1 I/F に 10+ メソッドあり、各テストでの差し替えパターンが多い
  - 使い捨ての one-shot 差し替えが散発的で、ダブルの再利用が効かない
  - `Verify(x => x.M(It.Is<Foo>(f => f.Bar == 42)), Times.Exactly(3))` のような呼び出しパターン検証が中心

### 代替案 B: Tests を Windows TFM (`net10.0-windows10.0.26100.0`) に上げて Infrastructure 実装を直接テスト

- 概要: Tests プロジェクトの TFM を Infrastructure に揃えて、`LocalFileSystem` 等を直接 new してテストする
- 採用しなかった理由: [ADR-0002](0002-clean-architecture-layered-projects.md) で「Tests は `net10.0` プラットフォーム中立を維持」と決定済み。Infrastructure のテストが必要になった時は別ターゲットの新プロジェクト (`SkimDownForWindows.Infrastructure.Tests`) を追加する余地を残す

### 代替案 C: 純粋サービスもダブル化する (フルモック方針)

- 概要: `MarkdownScanner` / `MarkdownTreeBuilder` / `InitialSelectionPicker` / `LinkResolver` もインターフェース化してダブルで差し替える
- 採用しなかった理由:
  - これらは副作用ゼロ、または `IFileSystem` のみに依存する純粋ロジックで、ダブル化する利点が薄い
  - `MainPageViewModel` のテストでは「Markdown ファイル → ツリー → 初期選択 → イベント発火」の一連の挙動を実体で通すほうが回帰検出力が高い
  - 抽象化のコストに対するメリットが小さい (テスト独立性の名のもとに偽の安心感を増やすだけ)

### 代替案 D: `MainPageViewModel` の `async void` ハンドラーを `internal Task RefreshTreeAsync()` 等に分解

- 概要: `OnTreeMayHaveChanged` をテスト直接呼べる `Task` 返り値メソッドに切り出し、テストで直接 `await`
- 採用しなかった理由 (現フェーズ):
  - 実装変更を伴うため次フェーズ候補。本 ADR の対象範囲外
  - 一方、将来 `InternalsVisibleTo` で Tests に internal を露出してこの方向に進む余地は残す
  - 現フェーズでは `WaitForSaveCountAsync` で対応する方が変更コストが低い

## 参考リンク

- [ADR-0002 クリーンアーキテクチャー風の層分割と DI コンテナの導入](0002-clean-architecture-layered-projects.md)
- MSTest: <https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-mstest>
- Moq の現状 (4.20 telemetry / SponsorLink 騒動以降): <https://github.com/devlooped/moq>
- NSubstitute: <https://nsubstitute.github.io/>
- FakeItEasy: <https://fakeiteasy.github.io/>
