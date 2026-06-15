# 技術ドキュメント (現状スナップショット)

このフォルダーは **SkimDown for Windows のいま** を中立に説明する技術リファレンスです。

- いま、どのプロジェクトがどの依存方向で繋がっているか
- いま、どのサービスがどのライフサイクルで登録されているか
- いま、どの API でどの永続化キーが書き換わるか

判断の歴史 (なぜそうしたか) は ADR に、ユーザー視点の使い方は README に、振る舞いの要件は SPEC に、コーディング規約は copilot-instructions に置かれます。**現状の構造説明**だけがここに集約されます。

## ドキュメント

| ファイル | 内容 |
|---|---|
| [architecture.md](architecture.md) | プロジェクト分割 / 層 / 依存方向 / ウィンドウスコープの全体像 |
| [dependency-injection.md](dependency-injection.md) | DI 登録一覧 / ライフサイクル / Ports/Adapters 対応表 |
| [markdown-content-pipeline.md](markdown-content-pipeline.md) | フォルダー走査 → ツリー化 → 選択 → リンク解決 → 変更検知 |
| [webview2-preview.md](webview2-preview.md) | 2 つの virtual host / メッセージプロトコル / 初期化 |
| [settings-and-state.md](settings-and-state.md) | `AppSettings` / `FolderState` / `RecentFolders` / 永続化境界 |
| [theming.md](theming.md) | システム / 組み込み / カスタムテーマ / `ResolvedTheme` / WebView2 への反映 |
| [activation-and-single-instance.md](activation-and-single-instance.md) | `Program.Main` / `AppInstance` redirect / `IWindowService` / single-file mode の起動経路 |
| [localization.md](localization.md) | `Strings/<locale>/Resources.resw` / `x:Uid` / `ResourceLoader` / サポートロケール / レイヤー境界 |

## このリポジトリのドキュメントランドスケープ

各ドキュメントは目的・読者・時系列スタンスが違います。何を聞きたいかでまず宛先を決めると速いです。

| 場所 | 読者 | 主題 | 時系列スタンス |
|---|---|---|---|
| [`README.md`](../README.md) | アプリ利用者 / 配布担当 | 機能・ビルド・Store 提出・カスタムテーマの書き方 | いま (利用者視点) |
| [`design/SPEC.md`](../design/SPEC.md) | アプリ実装者 / レビュアー | **振る舞いの要件** (ユーザーから見える挙動) | いま (要件) |
| `docs/` (このフォルダー) | アプリ実装者 / 新規参画者 | **実装構造の現状** (内部のクラス・I/F・状態モデル・メッセージプロトコル) | いま (実装) |
| [`.github/adr/`](../.github/adr/) | 設計判断のレビュアー | **なぜ** そうしたか | 歴史 (Accepted は不変) |
| [`.github/skills/`](../.github/skills/) | コードを書く人 / Copilot | コード変更時のチェックリスト・禁止事項 | いま (運用ルール) |
| [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) | コードを書く人 / Copilot | リポジトリ横断のコーディング規約 | いま (規約) |

### 何を読みたいかで決める

| 知りたいこと | 見る場所 |
|---|---|
| アプリの起動・操作方法 | `README.md` |
| 「フォルダーを開いた時に何が起きるべきか」 (要件) | `design/SPEC.md` |
| 「フォルダーを開いた時にどのクラスがどの順で呼ばれるか」 (現状実装) | `docs/markdown-content-pipeline.md` |
| 「なぜクリーンアーキテクチャー風に分割したか」 | `.github/adr/0002-clean-architecture-layered-projects.md` |
| 「新しいサービスをどこに置くか」 | `.github/skills/clean-architecture/SKILL.md` + `.github/copilot-instructions.md` |
| 「リリース手順」 | `README.md` + `.github/skills/release/SKILL.md` |

## 重複を避けるための役割境界

同じ題材でも切り口を変えて書きます。`docs/` を書く時の判別ガイド:

| 題材 | README | SPEC | docs | ADR |
|---|---|---|---|---|
| カスタムテーマ | 「Themes フォルダーに `*.json` を置く」「マッピング表」 | 「テーマ切替時に preview を再描画する」 | 「`ColorSchemeRegistry` が `IColorSchemeSource` から読み、`ResolvedTheme` を解決し、WebView2 に `{type: "theme", themeVars: ...}` を送る」 | 「VS Code 互換 JSON を採用した理由」 |
| Single-file mode | 「Explorer ダブルクリック / `skimdown README.md` / `skim README.md`」 | 「サイドバー強制非表示・relative link は新ウィンドウ」 | 「`Program.Main` → `AppInstance.FindOrRegisterForKey` → 二次インスタンスが `RedirectActivationToAsync`」 | 「`InitialActivation` 型を導入した理由」 |
| 設定永続化 | (触れない) | 「サイドバー幅・テーマ・ズーム倍率を永続化する」 | 「`JsonSettingsRepository` が `SemaphoreSlim` で single-flight、tmp + atomic move」 | (該当 ADR は無し) |
| ローカライズ (UI 文字列) | (触れない) | (現在は要件として明示せず) | 「`Strings/<locale>/Resources.resw` に集約、XAML は `x:Uid`、code は `ResourceLoader.GetForViewIndependentUse().GetString("Foo/Bar")`、Presentation 専有」 | 「MRT (resw) + `ResourceLoader` を採用した理由 / `IStringLocalizer` 抽象を採らなかった理由」 |

## 更新ライフサイクル (ドリフト対策)

`docs/` は ADR と違い「現状の真実」を書くため、コードと一緒に更新されないとすぐ嘘になります。次の変更を入れる PR は `docs/` の対応ページも更新の対象です。

| コードの変更 | 更新対象 |
|---|---|
| プロジェクト分割・TFM・参照関係の変更 | `architecture.md` |
| DI 登録の追加 / 削除 / ライフサイクル変更 (`AddSkimDownApplication` / `AddSkimDownInfrastructure` / `ServiceProviderFactory`) | `dependency-injection.md` |
| `Application/Abstractions/` の I/F 追加 / 削除 / シグネチャ変更 | `dependency-injection.md` (Ports / Adapters 対応表) |
| Markdown 走査・ツリー化・選択・リンク分類・変更検知ロジックの変更 | `markdown-content-pipeline.md` |
| WebView2 メッセージプロトコルの追加 / 削除 (`MarkdownPreview.xaml.cs` ↔ `web/renderer.js`) | `webview2-preview.md` |
| `AppSettings` / `FolderState` のフィールド追加 / 削除 / 既定値変更 | `settings-and-state.md` |
| カスタムテーマ解決 (`ColorMapping` / `ColorValueValidator` / `ResolvedTheme`) の変更 | `theming.md` |
| `Program.Main` / `AppInstance` redirect / `IWindowService` API の変更 | `activation-and-single-instance.md` |
| UI 文字列リソース (`Strings/<locale>/Resources.resw` 追加・削除 / `ResourceLoader` 利用箇所 / 新規ロケール) | `localization.md` |

### docs / ADR / SPEC が食い違った場合の真実

- **何が起きるかの要件 (期待挙動)** → `design/SPEC.md` を真実とする
- **いまの実装がどうなっているかの説明** → `docs/` を真実とする
- **なぜそうなっているかの判断履歴** → ADR を真実とする (古びていても本文は書き換えない)

3 つが矛盾する PR は「コードと SPEC が分かれている」「コードと ADR が分かれている」のどちらかを意味するので、`docs/` 単独で直すのではなく、要件 / 判断側も合わせて更新する。

## 書式

- 本文は **日本語**。コード片・型名・ファイルパス・URL は原文ママ。
- スタイルは **現在形・中立**。「〜してください」「〜してはいけません」は書かない (それは `.github/copilot-instructions.md` と `.github/skills/` の役割)。
- 図は **mermaid** を使う。GitHub UI で直接レンダリングされる構文 (`flowchart` / `graph` / `sequenceDiagram`) に限定し、過剰に増やさない。
- コードを参照する時は `[ファイル名](../src/...)` の相対リンクで貼り、シンボル名は本文中で実名で言及する (grep で辿れるように)。
- 各ドキュメントの末尾に「関連」セクションを置き、ADR / skill / SPEC / コードへのリンクを並べる。
