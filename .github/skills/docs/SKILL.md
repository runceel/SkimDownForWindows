---
name: docs-snapshot-skimdown
description: SkimDown for Windows の現状スナップショット (リポジトリルートの `docs/`) を追加・更新・レビューする時に使う。ADR (歴史) / SPEC (要件) / README (使い方) / copilot-instructions (規約) と別に「いまのコードがどう実装されているか」を中立に説明する技術リファレンスを維持する。トリガー語の例 - "docs/ を更新", "アーキ図を直す", "新しい I/F を足したので DI ドキュメント更新", "WebView2 メッセージプロトコル変えた", "AppSettings に項目追加", "テーマ解決を変えた", "activation flow を変えた", "現状スナップショット", "technical docs を書く"。
---

# docs/ Snapshot Maintenance for SkimDown for Windows

## どんな時に使うか

- リポジトリルートの `docs/` 配下の任意のページを書く・編集する時
- 次のいずれかを変更する PR を書く時 (= `docs/` の対応ページが古びる可能性が高い変更):
  - プロジェクト分割・TFM・参照関係
  - DI 登録 / ライフサイクル / `Application/Abstractions/` の I/F 追加 / 削除 / シグネチャ変更
  - WebView2 メッセージプロトコル (`MarkdownPreview.xaml.cs` ↔ `web/renderer.js`)
  - `AppSettings` / `FolderState` のフィールド追加 / 削除 / 既定値変更
  - `Program.Main` / `AppInstance` redirect / `IWindowService` API
  - `MarkdownScanner` / `MarkdownTreeBuilder` / `InitialSelectionPicker` / `LinkResolver` / `IFolderWatcher` ロジック
  - `ColorMapping` / `ColorValueValidator` / `ResolvedTheme` / `ColorSchemeRegistry` の挙動
  - UI 文字列リソース (`Strings/<locale>/Resources.resw` への追加・削除、`ResourceLoader` 利用箇所、新規ロケール追加)
- PR レビューで「コード変えてるのに `docs/` が古いまま」を指摘する時

`docs/` を **新規に作る** タスクではなく、**既存の `docs/` を維持する**ことが主眼。新規ドキュメント追加は既存 8 ファイルへの追記で済むケースが大半。

## 役割境界 (これだけは混ぜない)

| 場所 | 何を書くか |
|---|---|
| `.github/adr/` | **なぜ** そうしたか (判断の歴史) |
| `design/SPEC.md` | **何が起きるべきか** (要件) |
| `docs/` | **いまどう実装されているか** (現状スナップショット) |
| `README.md` | 利用者向けの使い方 / ビルド / 配布 |
| `.github/copilot-instructions.md` | 守るべきコーディング規約 |
| `.github/skills/*/SKILL.md` | タスク別チェックリスト + 具体例 |

`docs/` で **命令形を書かない** (それは copilot-instructions / SKILL の役割)。`docs/` で **要件を書かない** (それは SPEC の役割)。`docs/` で **判断理由を書かない** (それは ADR の役割。コードの説明のついでに判断理由が必要になったら `関連: ADR-NNNN` でリンクする)。

## コード変更 → 更新する docs 早見表

詳細マッピングと真実は [`docs/README.md` の「更新ライフサイクル」](../../../docs/README.md#更新ライフサイクル-ドリフト対策)。SKILL は要約だけ:

| コードの変更 | 更新対象 |
|---|---|
| プロジェクト分割 / TFM / 参照関係 | `docs/architecture.md` |
| DI 登録 / ライフサイクル / I/F 追加 | `docs/dependency-injection.md` |
| Markdown 走査 / ツリー / 選択 / リンク / 変更検知 | `docs/markdown-content-pipeline.md` |
| WebView2 host↔renderer メッセージプロトコル | `docs/webview2-preview.md` |
| `AppSettings` / `FolderState` / `settings.json` schema | `docs/settings-and-state.md` |
| テーマ解決 / VS Code key マッピング | `docs/theming.md` |
| `Program.Main` / `AppInstance` redirect / `IWindowService` | `docs/activation-and-single-instance.md` |
| UI 文字列 (`Strings/<locale>/Resources.resw`, `x:Uid`, `ResourceLoader`, 新規ロケール) | `docs/localization.md` |

上記のうち複数に該当する変更 (例: 新しい `I<X>` を Application に追加 + Infrastructure 実装 + DI 登録 + 既存 ViewModel から呼ぶ) は **複数ページの更新**になる。1 つだけ直して終わりにしない。

## 書き方の規約

| 観点 | 規約 |
|---|---|
| 言語 | 日本語 (本文)。コード片・型名・ファイルパス・URL は原文ママ |
| 文体 | **現在形・中立**。「〜してください」「〜してはいけない」は書かない |
| 主語 | "アプリは〜する"、"クラスは〜を持つ"。"開発者は〜" は禁止 (それは規約) |
| 図 | mermaid を使う。`flowchart` / `sequenceDiagram` の合計 **6 種程度** に留める (過剰メンテ回避) |
| コードへの参照 | 相対パスリンクで貼る (例: `[X](../src/.../X.cs)`)。シンボル名は本文中で実名で言及 (grep で辿れるように) |
| 関連リンク | 各ページ末尾に **"関連" セクション** を置き、ADR / skill / SPEC / コードへのリンクを並べる |
| 引用 | ADR の本文を `docs/` にコピペしない。理由が必要なら ADR にリンクで譲る |

## PR レビューチェックリスト

`docs/` を編集 or `docs/` の対応コードを編集する PR をマージする前に確認:

- [ ] 「コード変更 → 更新する docs 早見表」に該当する変更を全てカバーしている
- [ ] 文体が中立 (命令形が混ざっていない)
- [ ] 新規に追加したクラス名 / メソッド名 / I/F 名は `src/` に実在する
- [ ] 削除されたクラス名 / メソッド名 / I/F 名が `docs/` に残っていない
- [ ] 全相対リンク (`[..](../src/...)` / `[..](../.github/adr/...)` / `[..](other.md)`) が実在パスを指す
- [ ] mermaid コードブロックの構文が GitHub UI でレンダリングされる範囲 (`flowchart` / `graph` / `sequenceDiagram`) に収まる
- [ ] `docs/` の更新ライフサイクル表 (`docs/README.md`) も追従が必要なら更新した
- [ ] 「関連」セクションが古びていない (削除した ADR や SKILL を指していない)

## やってはいけないこと (Anti-patterns)

- `docs/` で命令形 (「〜してください」「〜は禁止」) を書く (`copilot-instructions.md` / SKILL の役割)
- 規約や運用ルールを `docs/` 本文に書く (DI 登録の禁止事項などはここではなく `copilot-instructions.md` / `clean-architecture/SKILL.md`)
- ADR の本文を `docs/` にコピペする (リンクで譲る)
- `docs/abstractions.md` のような **網羅一覧の独立ファイル** を増やす (ドリフトしやすい。`dependency-injection.md` の Ports/Adapters 表に集約)
- mermaid 図を 1 ページに何枚も追加する (図はコード変更で陳腐化しやすい。最重要 1 〜 2 枚に絞る)
- コードを変えるのに `docs/` を更新しない (`copilot-instructions.md` の「必ず守るルール」違反)
- 「要件」を `docs/` に書く (それは `design/SPEC.md`)
- 「判断理由」を `docs/` に書く (それは ADR。コードの "what" は書くが "why" は書かない)

## 関連

- 真実: [`docs/README.md`](../../../docs/README.md) — `docs/` の運用ライフサイクル表と役割境界マップ
- 上位規約: [`copilot-instructions.md`](../../copilot-instructions.md) — 「必ず守るルール」「ファイル配置のルール」「やってはいけないこと」
- 隣接 SKILL: [`clean-architecture/SKILL.md`](../clean-architecture/SKILL.md) (Application 層 / Infrastructure 層 / Presentation 層に何を置くか)
- ADR 運用: [`adr/README.md`](../../adr/README.md) (ADR は歴史、`docs/` はいまの真実)
