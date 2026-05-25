---
name: gh-cli-skimdown
description: gh CLI を使う時 (PR 作成 / Release 作成 / リポジトリ API 呼び出し) の認証・権限ハンドリング。Copilot CLI のデフォルトトークン (kaota_microsoft) は runceel/SkimDownForWindows に対して read-only で、PR / Release 作成が失敗する。runceel アカウントへの switch 手順、GH_TOKEN 優先順位、misleading なエラーメッセージの読み方をまとめる。トリガー語の例 - "PR を作る", "gh pr create が 403", "gh release create が 404", "workflow scope が必要", "Enterprise Managed User", "アカウント切替", "gh auth switch", "アカウントが READ-only", "リリースが作れない"。
---

# gh CLI authentication for SkimDown for Windows

## どんな時に使うか

- `gh pr create` で 403 / Forbidden が出た時
- `gh release create` で 404 / `"workflow" scope may be required` が出た時
- `gh api` でリポジトリの labels / projects / settings を変更したい時
- Copilot CLI から `gh` 経由で書き込み操作を実行したい時

## 背景 (なぜこの skill が必要か)

このリポジトリは個人アカウント `runceel/SkimDownForWindows` 配下にある。
Copilot CLI のワークスペースは別アカウント (例: `kaota_microsoft`) 経由で起動されており、そのアカウントの OAuth トークンは `runceel/SkimDownForWindows` に対して **`pull: true` / `push: false`** な READ-only 権限しか持たない。

ブランチの **push 操作** は Copilot Workspaces のサービスプリンシパル経由で別ルートで通る (= `git push` は成功する) が、**PR / Release / Issue / リポジトリ設定変更** などの GitHub REST/GraphQL API を経由する操作は失敗する。

```powershell
# 現在のトークンの実効権限を確認するワンライナー
gh api /repos/runceel/SkimDownForWindows --jq '.permissions'
# => { "admin": false, "maintain": false, "pull": true, "push": false, "triage": false }
```

## 認証アカウント一覧

`gh auth status` で以下の 3 エントリが見えるはず:

| アカウント | 由来 | 主用途 | 書き込み |
|---|---|---|---|
| `kaota_microsoft` (GH_TOKEN) | Copilot CLI が環境変数で active 化 | 読み取り / ブランチ push (Workspaces 別ルート) | ❌ |
| `kaota_microsoft` (keyring) | 既存ログイン (バックアップ) | 読み取り | ❌ |
| `runceel` (keyring) | リポジトリ所有者ログイン | **PR / Release / Issue / settings 変更** | ✅ |

## アカウント切替手順

`GH_TOKEN` 環境変数は **`gh auth switch` よりも常に優先される**。`gh auth switch` だけでは effective なアカウントを変えられないため、環境変数を一旦外す必要がある。

```powershell
# 1) 環境変数を現在のシェルから外す
Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue

# 2) keyring の runceel に切替
gh auth switch -u runceel

# 3) 書き込み操作を実行
gh pr create --repo runceel/SkimDownForWindows --base main --head <branch> --title "..." --body-file <path>
# または
gh release create v<X.Y.Z> --draft --prerelease --target <branch> --notes-file <path> <asset> <asset>

# 4) 新しい powershell セッションでは GH_TOKEN が自動再注入されるので戻し作業は基本不要
#    同じシェルで戻す場合のみ:
gh auth switch -u kaota_microsoft
```

> **注意**: `Remove-Item Env:\GH_TOKEN` を使うこと。`$env:GH_TOKEN = $null` では「空文字列で存在する」状態になり、`gh` から「設定されている」と扱われてしまうので無効。

## エラーメッセージの読み方

`gh` のエラー文言は本当の原因を覆い隠していることが多い。次の対応表で読み替える。

| エラー文言 | 実際の原因 | 対処 |
|---|---|---|
| `Failed to create release, "workflow" scope may be required.` | repo write 権限が無い (misleading) | `runceel` に switch (上記手順) |
| `403 Forbidden: As an Enterprise Managed User, you cannot access this content` | EMU アカウントから個人リポジトリへの API アクセス | `runceel` に switch |
| `404 Not Found` on `/repos/<owner>/<repo>/releases` | repo write 権限が無いと GitHub は 404 を返すことがある | `gh api /repos/<owner>/<repo> --jq '.permissions'` で `push` を確認 → switch |
| `HTTP 401 Bad credentials` | トークンが失効 / 取り消し | `gh auth refresh -h github.com` |

## やってはいけないこと

1. **`gh auth refresh -s workflow` の指示に従って `kaota_microsoft` に scope を追加する**
   - スコープではなく **permission** の問題。やっても解決しない (同じ 403/404 が出続ける)
2. **`runceel` を active のまま `git push` を続ける**
   - Copilot Workspaces 想定の `kaota_microsoft` push ルートと混ざり、ブランチ所有者やマシン認証情報が一致しなくなる場合がある。書き込み API を叩いたら `gh auth switch` で戻すか、シェルを閉じる
3. **`runceel` アカウントを keyring から削除する**
   - 次回も切替不可になる。default の `kaota_microsoft` (GH_TOKEN) のまま放置で OK
4. **`$env:GH_TOKEN = $null` で環境変数を外そうとする**
   - PowerShell の仕様で「空文字列のまま存在する」扱いとなり、`gh` は環境変数が設定されていると判断する。必ず `Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue` を使う
5. **`gh pr create` の `--repo` 引数を省略する**
   - Copilot ワークツリーは fork ではなく upstream を直接 clone しているが、リモート URL や push 先の解釈に依存して `gh` が誤った repo を選ぶことがある。常に `--repo runceel/SkimDownForWindows` を明示

## 典型ワークフロー

### PR 作成

```powershell
Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue
gh auth switch -u runceel
gh pr create `
    --repo runceel/SkimDownForWindows `
    --base main `
    --head <feature-branch> `
    --title "<conventional commit subject>" `
    --body-file <path-to-body.md>
```

### Release 作成 (sideload 配布)

```powershell
Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue
gh auth switch -u runceel
gh release create v<X.Y.Z> `
    --draft `
    --prerelease `
    --target <branch-or-sha> `
    --title "SkimDown for Windows v<X.Y.Z>" `
    --notes-file <path-to-release-notes.md> `
    .\bin\StorePackage\45014okazuki.SkimDown_<ver>_sideload.msixbundle `
    .\bin\StorePackage\devcert.cer
```

### Issue 作成 / コメント / ラベル変更

書き込み操作なら全部同じパターン。`Remove-Item Env:\GH_TOKEN` → `gh auth switch -u runceel` → 操作。

## 関連

- [`release/SKILL.md`](../release/SKILL.md) — リリース手順 (本 skill の `gh release create` 部分を含む)
- [`copilot-instructions.md` "よくある落とし穴"](../../copilot-instructions.md) — 全タスク横断の高レベル指針
