# Skills for SkimDown for Windows

このフォルダーには、SkimDown for Windows のリポジトリ固有 skill を置く。

skill は **[ADR](../adr/)** と **[copilot-instructions.md](../copilot-instructions.md)** で定めた方針を、コーディング時にすぐ参照できるよう**チェックリスト + 具体例 + やってはいけないこと**の形に再構成したもの。意思決定の歴史性は ADR を真実とし、skill は ADR の要約として位置づける。

## 目的

- 新規機能追加 / リファクタ / レビュー時に、何を Where に配置すべきかを毎回 ADR を読み返さずに済むようにする
- 「テストダブルをどう書くか」「`async void` の完了をどう待つか」など、実装パターンの再発明を防ぐ
- 人間と Copilot CLI の双方が同じガイドを参照できるようにする (リポジトリ内に置く意義)

## 一覧

| Skill | 用途 | 対応 ADR | 対応 copilot-instructions セクション |
|---|---|---|---|
| [`clean-architecture/SKILL.md`](clean-architecture/SKILL.md) | 層境界 / DI / 抽象配置 / 新規サービス追加 | [ADR-0002](../adr/0002-clean-architecture-layered-projects.md) | "アーキテクチャー (要点)", "必ず守るルール", "ファイル配置のルール" |
| [`unit-test/SKILL.md`](unit-test/SKILL.md) | Application / Domain 単体テスト追加 / TestHelpers パターン | [ADR-0003](../adr/0003-test-strategy-and-testhelpers-pattern.md) | "ビルド・テスト・実行" |

## 書き方の規約

- 各 skill は `<name>/SKILL.md` という形式で配置する
- ファイル先頭に YAML フロントマターで `name`, `description` を書く (他の Copilot skill 慣習に合わせる)
- 本文は **日本語**
- ADR と矛盾する内容を書かない。矛盾する場合は ADR を新規作成して skill を更新する
- ADR にまだ載っていない実装パターンを skill に書いてはいけない (ADR 先行)

## 関連

- [ADR README](../adr/README.md): ADR の運用ルール
- [copilot-instructions.md](../copilot-instructions.md): 全体方針 (skill より高い優先度)
