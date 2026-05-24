# Architecture Decision Records (ADR)

このフォルダーには、SkimDown for Windows における**アーキテクチャー上の重要な設計判断**を ADR（Architecture Decision Record）として記録する。

> ADR を書くという判断自体は [`0001-record-architecture-decisions.md`](0001-record-architecture-decisions.md) を参照。

## 何を ADR にするか

書く対象:

- プロジェクト構成や層境界に影響する判断
- 外部依存（ライブラリ、コンテナ、シリアル化形式、ストレージ方式 等）の選定・置き換え
- 横断的な命名・配置・コーディング規約のポリシー変更
- セキュリティモデル（例: WebView2 の二重 origin、入力バリデーション境界）の変更
- 既存 ADR を変更・無効化する判断

書かない対象:

- ローカル変数のリネーム、メソッド抽出、内部ヘルパー追加など局所的なリファクタリング
- バグ修正そのもの（バグ修正に伴って設計を変える時はその変更分だけ ADR にする）
- ドキュメント表記の修正、フォーマッタ走らせた程度の変更

## ファイル名・連番

- ファイル名: `NNNN-kebab-case-title.md`（4 桁ゼロ詰めの連番 + ケバブケースの英語タイトル）
- 番号は欠番を作らない。Superseded（廃止）になった ADR もファイル自体は残す
- タイトルは英語、本文は日本語

例:

```
.github/adr/
├─ README.md
├─ template.md
├─ 0001-record-architecture-decisions.md
├─ 0002-clean-architecture-layered-projects.md
└─ 0003-...
```

## ライフサイクル

ADR は次のいずれかのステータスを持つ。

- **Proposed** — PR レビュー中で、まだ採用が確定していない
- **Accepted** — マージされ、コードベースで有効
- **Deprecated** — 設計判断としては撤回されたが、後続 ADR が存在しない（積極的には推奨しないが、置き換え方針も決まっていない）
- **Superseded by NNNN** — 別の ADR に置き換わった

**変更ルール**:

- Accepted な ADR の**本文を書き換えてはいけない**（決定の歴史性を保つため）
- 撤回したい時は新規 ADR を起こし、旧 ADR の `ステータス` を `Superseded by NNNN` に書き換える（リンクの追加のみ許可）
- typo・リンク切れ修正は本文書き換えとはみなさない

## 書き方

1. `template.md` をコピーして `NNNN-kebab.md` にリネーム
2. ヘッダーの番号・タイトル・日付・ステータスを更新
3. **コンテキスト**（なぜこの判断が必要になったか）を書く
4. **決定**（何を決めたか）を書く
5. **結果（Consequences）**（決定によって何が変わるか、メリット・デメリット）を書く
6. **検討した代替案**を書く（少なくとも 2 つ以上、採用案以外の選択肢）
7. **参考リンク**があれば追加

PR で他のレビュワーから合意を得たら `ステータス` を `Accepted` に変更してマージする。

## 既存の ADR

<!-- 新しい ADR を追加したら、ここに 1 行ずつ追記する -->

| 連番 | タイトル | ステータス |
| --- | --- | --- |
| [0001](0001-record-architecture-decisions.md) | アーキテクチャー判断を ADR として記録する | Accepted |
| [0002](0002-clean-architecture-layered-projects.md) | クリーンアーキテクチャー風のプロジェクト分割と DI 導入 | Accepted |
