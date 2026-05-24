---
name: unit-test-skimdown
description: SkimDown for Windows の Application / Domain 単体テストを追加・改修・レビューする時に使う。トリガー語の例 - "テストを追加", "MainPageViewModel のテスト", "TestHelpers", "Stub / Fake / Recording / InMemory", "async void のテスト", "Sleep を使いたい", "テスト用に IFoo を差し替えたい", "MarkdownScanner のテスト"。テスト戦略の根拠は ADR-0003 を参照。
---

# Unit Test for SkimDown for Windows

## どんな時に使うか

- 新しいテストを `SkimDownForWindows.Tests` に追加する時
- 新しい `I<X>` 抽象を足したのでテスト用ダブルを書く時
- `MainPageViewModel` 等の `async void` ハンドラーをテストする時
- 既存テストを直す時に「これは仕様なのか現挙動の golden test なのか」を区別する時
- PR レビューで「モックライブラリ入れたい」と提案が出た時

判断の正当性 (なぜこのテスト戦略になっているか) は **[ADR-0003](../../adr/0003-test-strategy-and-testhelpers-pattern.md)** を参照。skill はそこで決まったことを実装時に**思い出すための圧縮表**。

## テストプロジェクトの前提

| 項目 | 内容 |
|---|---|
| プロジェクト | `src/SkimDownForWindows.Tests/SkimDownForWindows.Tests.csproj` |
| TFM | **`net10.0`** (プラットフォーム中立、Windows TFM ではない) |
| テストフレームワーク | **MSTest** v4 |
| 並列実行 | **`MethodLevel` Parallelize** (`MSTestSettings.cs` で設定) |
| 参照プロジェクト | `SkimDownForWindows.Domain` / `SkimDownForWindows.Application` のみ |
| **参照しない** プロジェクト | `SkimDownForWindows.Infrastructure` (Windows TFM のため) |
| Implicit usings | `Microsoft.VisualStudio.TestTools.UnitTesting` を `<Using>` で取り込み済み |

> Tests から Infrastructure を `ProjectReference` してはいけない。`IFileSystem` が必要なテストは `TestHelpers/RealFileSystem` を使う。

## テストダブルの基本ポリシー

- **モックライブラリ (Moq / NSubstitute / FakeItEasy) は採用しない**
- 各 `I<X>` 抽象に対する **手書き in-memory 実装** を `src/SkimDownForWindows.Tests/TestHelpers/` に置く
- 1 ファイル = 1 ダブル、`internal sealed`
- 内部名前空間 = `SkimDownForWindows.Tests.TestHelpers`

理由は [ADR-0003 § 1](../../adr/0003-test-strategy-and-testhelpers-pattern.md) 参照 (リファクタの検出力、AOT 親和性、再利用効率)。

## 命名規約 (Prefix)

意図に応じて prefix を使い分ける。

| Prefix | 用途 | 内部状態 | 例 |
|---|---|---|---|
| `Stub<X>` | 入力に対して**固定値を返すだけ** | なし or 設定値のみ | `StubMarkdownFileReader`, `StubSystemThemeProvider` |
| `Fake<X>` | 内部状態を持ち、テストから**状態を進めたり外部イベントを発火**したりできる | あり | `FakeFolderWatcher` (`RaiseTreeMayHaveChanged` 等) |
| `Recording<X>` | 副作用を持つ I/F 呼び出しを**記録するだけ** | 呼び出し履歴のみ | `RecordingShellService`, `RecordingClipboardService` |
| `InMemory<X>` | 永続化や読み書きを伴う I/F の**メモリ実装** | データ + 呼び出し履歴 + waiter | `InMemorySettingsRepository` |
| `Real<X>` | 実体に近い実装をテスト用にラップしたもの (Infrastructure 不参照の代替) | 実 OS リソース | `RealFileSystem` |

## 既存ダブル一覧

> 場所: `src/SkimDownForWindows.Tests/TestHelpers/`

| ダブル | 対応する抽象 | 主要機能 |
|---|---|---|
| `InMemorySettingsRepository` | `ISettingsRepository` | `Current` で `AppSettings` 保持、`SaveAsync` / `FlushSync` 回数記録、**`WaitForSaveCountAsync(int, TimeSpan?)`** で非同期完了待ち |
| `FakeFolderWatcher` | `IFolderWatcher` | `Watch` / `Stop` / `Dispose` 回数記録、`LastWatchedPath`、**`RaiseTreeMayHaveChanged()` / `RaiseFileContentChanged(path)`** でテスト側からイベント発火 |
| `StubMarkdownFileReader` | `IMarkdownFileReader` | `SetContent(path, content)` で応答を仕込み、未登録パスは `DefaultContent` を返す。`ReadCalls` で履歴取得 |
| `StubSystemThemeProvider` | `ISystemThemeProvider` | `System` プロパティで OS テーマの戻り値を切り替え |
| `RecordingClipboardService` | `IClipboardService` | `Writes` / `LastWrite` で呼び出し履歴を保持 |
| `RecordingShellService` | `IShellService` | `RevealedPaths` / `LastRevealedPath` で呼び出し履歴を保持 |
| `RealFileSystem` | `IFileSystem` | 本物の `System.IO.*` を使う。例外は吸収して `false` / 空列挙を返す (`LocalFileSystem` と同じ quiet behavior) |

## 新規ダブルを追加する 4 ステップ

### Step 1: 命名 prefix を選ぶ

- 固定値を返すだけ → `Stub<X>`
- イベント発火させたい → `Fake<X>`
- 呼び出し履歴だけ取りたい → `Recording<X>`
- データの読み書きを伴う → `InMemory<X>`
- 本物の OS 機能を使う必要がある → `Real<X>` (最後の手段)

### Step 2: ファイルを `TestHelpers/` 配下に作成

```csharp
// src/SkimDownForWindows.Tests/TestHelpers/InMemoryXxx.cs
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// ...用途と特殊な完了待ち API を XML doc コメントで明記...
/// </summary>
internal sealed class InMemoryXxx : IXxx
{
    private readonly object _gate = new();
    ...
}
```

- `internal sealed`
- スレッドセーフ性を `lock` で確保 (`MethodLevel` Parallelize と衝突しないように)
- 副作用観測 API (`Writes`, `WaitForXxxAsync`) を提供

### Step 3: 副作用観測 API を作る (必要なら)

- 単純な呼び出し履歴 → `List<T> Calls` プロパティ
- `async` 副作用の完了待ち → **`WaitForXxxCountAsync` パターン** (次節参照)
- 直近値だけ欲しい → `LastXxx` プロパティ

### Step 4: テストで利用

```csharp
[TestInitialize]
public void Setup()
{
    _xxx = new InMemoryXxx();
    ...
}
```

## `async void` / fire-and-forget の完了同期

`MainPageViewModel` には次のような fire-and-forget ハンドラーがある:

- `async void OnTreeMayHaveChanged(...)` (`IFolderWatcher.TreeMayHaveChanged` 購読)
- `_ = SelectAndLoadAsync(...)` (UI コマンド経由)
- これらが内部で `ISettingsRepository.SaveAsync()` を呼ぶ

テスト側はこれを `await` できないので **観測点側に完了通知を仕込む**。

### ルール

- ❌ **`Thread.Sleep` / `Task.Delay` ベースのポーリングは禁止** (flaky テストの温床)
- ✅ ダブル側に **`WaitForXxxCountAsync(int expected, TimeSpan? timeout = null)`** を実装

### `InMemorySettingsRepository.WaitForSaveCountAsync` の仕組み

```csharp
public Task SaveAsync()
{
    List<TaskCompletionSource<int>> toFire;
    int count;
    lock (_gate)
    {
        _saveAsyncCalls++;
        count = _saveAsyncCalls;
        toFire = new List<TaskCompletionSource<int>>(_waiters);
        _waiters.Clear();
    }
    foreach (var w in toFire) w.TrySetResult(count);
    return Task.CompletedTask;
}

public async Task WaitForSaveCountAsync(int expectedCount, TimeSpan? timeout = null)
{
    var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
    while (true)
    {
        TaskCompletionSource<int> tcs;
        lock (_gate)
        {
            if (_saveAsyncCalls >= expectedCount) return;
            tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(tcs);
        }
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException($"SaveAsync was called {SaveAsyncCalls} times; expected at least {expectedCount}.");
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(remaining)).ConfigureAwait(false);
        if (completed != tcs.Task)
            throw new TimeoutException($"SaveAsync was called {SaveAsyncCalls} times; expected at least {expectedCount}.");
    }
}
```

### テスト側での使い方

```csharp
[TestMethod]
public async Task OpenFolderAsync_PersistsRecentFolder()
{
    var vm = CreateViewModel();

    await vm.OpenFolderAsync(_root);

    // 初回 Open で SaveAsync が 1 回呼ばれることを Sleep 無しで確認
    await _settings.WaitForSaveCountAsync(1);
    Assert.AreEqual(_root, _settings.Current.LastFolderPath);
}
```

タイムアウト超過時の例外メッセージには**実際の呼び出し回数を含める** (`TimeoutException` の `Message` がテストレポートで読みやすくなる)。

## 一時ディレクトリのライフサイクル

実ファイルシステムを使うテスト (`MarkdownScanner` 等) は固有の一時ディレクトリを使う。

```csharp
[TestClass]
public sealed class MyTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "skim-myname-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Teardown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Touch(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
```

ポイント:

- ディレクトリ名 = `"skim-<area>-" + Path.GetRandomFileName()` で衝突回避
- `[TestInitialize]` 作成、`[TestCleanup]` 削除
- 削除は best-effort (`try { ... } catch { }`)。Windows のロックで失敗してもテスト結果に影響させない
- `MethodLevel` Parallelize と衝突しない (テストインスタンスごとに別ディレクトリ)

## 純粋サービスは実体を使う (ハイブリッド方針)

`MainPageViewModel` のテストで Markdown 解析パイプラインを通すとき、純粋サービスは**ダブル化せず実体を `new` する**。

```csharp
private MainPageViewModel CreateViewModel()
{
    var scanner = new MarkdownScanner(_fs);           // 実体 + RealFileSystem
    var treeBuilder = new MarkdownTreeBuilder();      // 実体
    var picker = new InitialSelectionPicker();        // 実体
    var linkResolver = new LinkResolver();            // 実体
    return new MainPageViewModel(
        _settings, _watcher, _reader, _fs,
        _shell, _clipboard, _theme,
        scanner, treeBuilder, picker, linkResolver);
}
```

理由:

- これらは副作用ゼロ or `IFileSystem` のみに依存する**純粋ロジック**
- ダブル化すると「Markdown ファイル → ツリー → 初期選択」の統合挙動の回帰検出力が落ちる
- I/O 抽象だけダブル化すれば十分制御可能

## 仕様要件 vs 現挙動 (golden test) の命名

- 仕様要件 (SPEC で要求される動作):
  - 名前: `Action_State_ExpectedOutcome` (例: `OpenFolderAsync_WithEmptyFolder_SetsHasFolderTrue`)
- 現挙動の固定化 (バグ寄りだが現状そう動く / 意図的だが仕様には書かれていない):
  - 名前に `_CurrentBehavior` / `_Documents...Behavior` を含める
  - コメントで「仕様で要求されているわけではない」旨を明記
  - 仕様変更を伴う修正は別 PR + 必要なら新 ADR

```csharp
[TestMethod]
public void Scan_HiddenDotFolder_IsExcluded_CurrentBehavior()
{
    // SPEC: 隠しフォルダーの扱いは厳密には未定義だが、現状 .git や .vscode を除外している。
    // 本テストはこの除外挙動を固定化する。仕様変更時はテスト名から _CurrentBehavior を外す。
    ...
}
```

## テスト対象外 (現フェーズ)

- **Infrastructure 層**の I/O ラッパー (`LocalFileSystem`, `JsonSettingsRepository` 等)
  - Tests が `net10.0` で Infrastructure (`net10.0-windows*`) を参照しないため
  - 必要になったら別 TFM の `SkimDownForWindows.Infrastructure.Tests` プロジェクトを新設
- **Presentation 層** (`MainWindow`, `MainPage`, `MarkdownPreview`)
  - WinUI 3 が UI スレッド前提のため `net10.0` テストでは扱えない
- **Domain 層** の純粋 enum / record
  - 値そのもののテストは過剰。JSON ラウンドトリップ等で間接的に保持を確認

## やってはいけないこと (Anti-patterns)

### ❌ モックライブラリの導入

```xml
<!-- ❌ NG -->
<PackageReference Include="Moq" Version="..." />
```

理由は ADR-0003 § 検討した代替案 A 参照。導入したくなった場合は **ADR を新規作成**して議論する。

### ❌ `Thread.Sleep` / `Task.Delay` で非同期を待つ

```csharp
// ❌ NG - flaky テストの温床
await Task.Delay(500);
Assert.AreEqual(1, _settings.SaveAsyncCalls);
```

```csharp
// ✅ OK
await _settings.WaitForSaveCountAsync(1);
```

### ❌ Infrastructure を `ProjectReference`

```xml
<!-- ❌ NG - Tests の TFM は net10.0、Infrastructure は net10.0-windows10.0.26100.0 -->
<ProjectReference Include="..\SkimDownForWindows.Infrastructure\..." />
```

```csharp
// ✅ OK - TestHelpers/RealFileSystem を使う
var fs = new RealFileSystem();
```

### ❌ 共有可変状態を持つテストクラス

```csharp
// ❌ NG - MethodLevel Parallelize と衝突
private static InMemorySettingsRepository _sharedSettings = new();
```

```csharp
// ✅ OK - インスタンスフィールド + [TestInitialize]
private InMemorySettingsRepository _settings = null!;
[TestInitialize] public void Setup() => _settings = new InMemorySettingsRepository();
```

### ❌ 純粋サービスをダブル化

```csharp
// ❌ NG - 過剰な抽象化
public interface IMarkdownScanner { ... }
internal sealed class StubMarkdownScanner : IMarkdownScanner { ... }
```

```csharp
// ✅ OK - 実体を new
var scanner = new MarkdownScanner(_fs);
```

### ❌ ダブルを `TestHelpers/` 外に置く

- どのテストファイルが何を差し替えているかが追えなくなる
- 命名規約も崩れる

```csharp
// ✅ OK
// src/SkimDownForWindows.Tests/TestHelpers/InMemoryXxx.cs
```

## チェックリスト (PR 時セルフレビュー)

- [ ] 新規ダブルは `TestHelpers/` 配下に置いたか
- [ ] ダブルの prefix (Stub / Fake / Recording / InMemory / Real) は意図に合っているか
- [ ] ダブルは `internal sealed` か
- [ ] 並列実行に対応する `lock` / スレッドセーフな構造になっているか
- [ ] `Thread.Sleep` / `Task.Delay` を使った非同期待ちが無いか
- [ ] 一時ディレクトリは固有名 + cleanup ありか
- [ ] 純粋サービス (`MarkdownScanner` 等) を不要に抽象化していないか
- [ ] テスト名は仕様要件 / 現挙動 (`_CurrentBehavior`) を区別しているか
- [ ] Infrastructure を `ProjectReference` していないか

## ビルド・テストの実行

```powershell
# 単体テスト (net10.0)
dotnet test src\SkimDownForWindows.Tests

# 上流 samples を使う統合テスト (オプトイン)
$env:SKIM_SAMPLES_PATH = "C:\path\to\SkimDown\samples"
dotnet test src\SkimDownForWindows.Tests
```

## 参考リンク

- [ADR-0003 Application / Domain 単体テストの戦略と TestHelpers パターン](../../adr/0003-test-strategy-and-testhelpers-pattern.md)
- [ADR-0002 クリーンアーキテクチャー風の層分割と DI コンテナの導入](../../adr/0002-clean-architecture-layered-projects.md)
- [copilot-instructions.md](../../copilot-instructions.md)
- [clean-architecture skill](../clean-architecture/SKILL.md)
- [MSTest documentation](https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-mstest)
