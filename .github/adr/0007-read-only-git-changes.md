# 0007. Read-only Git changes through an Infrastructure adapter

- 日付: 2026-10-29
- ステータス: Proposed
- 関連 ADR: 0002, 0003, 0006

## コンテキスト

上流 SkimDown v0.16.0 は、開いた working-tree root と HEAD を比較する任意表示を導入する。
Markdown フォルダーとリポジトリ設定は信頼できない入力であり、読み取り専用ビューアーが
Git hook、filter、external diff、認証プロンプトを実行することは意図しない。
Windows には macOS の `/usr/bin/git` に相当する固定パスがない。

## 決定

Git の実行と参照監視はウィンドウスコープの Infrastructure adapter に閉じ、
Application は抽象を通じて HEAD の本文とファイル状態を受け取る。
Git 表示設定はアプリ全体で共有し、既定は無効とする。

Git 実行ファイルは絶対 PATH エントリから解決し、current directory と開いたフォルダー配下を
候補から除外し、reparse point 経由の候補も除外する。絶対実行ファイル名と引数リストを使い、shell は使わない。
子環境を最小限から構築し、fsmonitor、lazy fetch、任意 lock、対話を無効にする。
read-only plumbing のみを用い、working file の blob hash はアプリ内で計算する。
コマンドには時間・出力量の上限を設け、失敗はログに記録して通常の Markdown 表示を維持する。

以前の Markdown は既存の JSON bridge で渡し、renderer は plain text として表示する。
WebView2 の二重 origin と既存の sanitize 境界は変更しない。

## 結果（Consequences）

### ポジティブ

- リポジトリ設定にある実行プログラムを比較処理が起動しない。
- Git がない環境でも通常の閲覧機能は利用できる。
- Application の比較・装飾・更新制御を OS 非依存でテストできる。

### ネガティブ

- 任意の clean filter を適用しないため、filter 処理後の比較とは一致しない場合がある。
- Git for Windows のインストールと PATH 登録が必要になる。

### ニュートラル

- HEAD と現在の本文の比較であり、staged / unstaged の二段階 diff は提供しない。

## 検討した代替案

### 代替案 A: git status / git diff

簡潔だが、fsmonitor、filter、external diff などの実行経路を持つため採用しない。

### 代替案 B: Git ライブラリの追加

外部プロセスを不要にできる一方、ネイティブ依存、配布、Git format 互換性の管理が増える。
上流の plumbing 方針との整合性を優先する。

## 参考リンク

- [07JP27/SkimDown#59](https://github.com/07JP27/SkimDown/pull/59)
- [現在の実装](../../docs/markdown-content-pipeline.md)
