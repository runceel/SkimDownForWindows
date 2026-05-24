# 0001. アーキテクチャー判断を ADR として記録する

- 日付: 2026-05-24
- ステータス: Accepted
- 関連 ADR: なし

## コンテキスト

SkimDown for Windows は WinUI 3 アプリとしてここ数ヶ月で形を整えてきた。

- 当初は単一プロジェクト構成で、ファイル操作・プロセス起動・クリップボード・テーマ判定などの I/O が ViewModel に混入していた
- `static` クラス・`static` プロパティでプロセス全体の状態を持っており、テスト容易性が低かった
- DI コンテナを導入していなかったため、`MainPage` や `MainPageViewModel` で多くの具象クラスを `new` していた

これからリファクタリングを進めるにあたり、

- 「なぜそういう構造にしたのか」を後から追えるようにする
- 設計の前提が変わった時に、過去の決定を置き換えやすくする

ことを目的に、**アーキテクチャー上の判断を文書として残す仕組み**を導入したい。

## 決定

`.github/adr/` 配下に **Architecture Decision Record (ADR)** を時系列で蓄積する。

- フォーマットは **MADR (Markdown Architectural Decision Records) の lite 派生**
  - ヘッダー: `# NNNN. <タイトル>`、日付、ステータス、関連 ADR
  - 本文: コンテキスト / 決定 / 結果（Consequences）/ 検討した代替案 / 参考リンク
- ファイル名: `NNNN-kebab-case-title.md`（連番は 4 桁ゼロ詰め）
- 本文は **日本語**、タイトル英語表記
- ステータスは `Proposed` / `Accepted` / `Deprecated` / `Superseded by NNNN` のいずれか
- Accepted 後の本文書き換えは禁止（typo・リンク修正は除く）
- 既存 ADR を撤回する時は新規 ADR を起こす
- 運用ルール本体・テンプレートは `.github/adr/README.md` と `.github/adr/template.md` に置く

「ADR を書く」対象は以下のような判断:

- プロジェクト構成・層境界に影響するもの
- 外部依存の選定・置き換え
- 横断的な命名・配置ポリシー変更
- セキュリティモデルの変更
- 既存 ADR の置き換え

trivial な局所変更（変数名、メソッド抽出、ヘルパー追加 等）は ADR の対象外。

## 結果（Consequences）

### ポジティブ

- 設計判断の意図と背景がコードベースと同じ場所にバージョン管理される
- 新規メンバー（人間でも Copilot でも）が現在のアーキテクチャーに至った経緯を辿れる
- 将来の設計変更が「なぜそうなっているのか」の答えを ADR から得られる
- レビュー時に「これ ADR を起こすべき？」という議論が一貫した基準で行える

### ネガティブ

- 設計判断のたびにファイル作成のオーバーヘッドが発生する
- 「ADR を書くべき判断か否か」自体に判断コストが発生する
- 本文書き換え禁止ルールにより、後追いの修正には新規 ADR が必要

### ニュートラル

- `.github/adr/` 配下に増え続けるため、README 側に「既存 ADR 一覧」を持たせる必要がある
- ADR 本文の翻訳はしないため、日本語以外の読者には機械翻訳が必要

## 検討した代替案

### 代替案 A: `docs/adr/` 配下に置く

- 概要: Industry-standard な配置場所
- 採用しなかった理由: 本プロジェクトの所有者が `.github/adr/` を指定したため。GitHub の `.github/` 配下は Copilot Coding Agent などのツールから優先的に参照されるメリットもある。README から明示的にリンクすることで人間の発見性は補う

### 代替案 B: ADR を書かず、PR 説明文 / コミットメッセージ / Issue で代用する

- 概要: 軽量で、GitHub 標準機能だけで済む
- 採用しなかった理由: 設計判断の検索性が低く、リポジトリのクローン単体で完結しない。歴史性も弱い

### 代替案 C: Nygard 風（Status / Context / Decision / Consequences のみ）

- 概要: ADR の最も古典的なフォーマット。最小限
- 採用しなかった理由: 「検討した代替案」の節がないと、後から「なぜこの選択肢を選んだか」が分かりづらい。MADR-lite は代替案セクションが標準化されており、僅かなオーバーヘッドで歴史性が増える

### 代替案 D: フルの MADR（Decision Drivers、Pros and Cons of the Options 等を含む完全版）

- 概要: より詳細な評価フレームワーク
- 採用しなかった理由: 本プロジェクトの規模では過剰。lite 派生で十分

## 参考リンク

- MADR: <https://adr.github.io/madr/>
- Nygard's original ADR post: <https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions>
- ADR GitHub organization: <https://adr.github.io/>
