---
name: release-skimdown
description: SkimDown for Windows の新バージョンを Microsoft Store に提出 + GitHub Releases に sideload 用ビルドを公開する時に使う。トリガー語の例 - "リリースを切る", "バージョンを上げる", "Store 提出版を作る", "msixupload", "msixbundle", "GitHub Releases に公開", "sideload 配布", "devcert", "新機能のチェック", "リリースノートを書く", "署名検証", "WACK"。配布物の分離 (Store 無署名 / sideload 自己署名) と Store 版への移行リスクを守るための圧縮チェックリスト。
---

# Release for SkimDown for Windows

## どんな時に使うか

- 新バージョンを Microsoft Store に提出する時 (`.msixupload` を作る)
- 新バージョンを GitHub Releases にも公開する時 (sideload 用 `_sideload.msixbundle` + `devcert.cer` を添付)
- バージョン番号をインクリメントする時 (Store 制約のチェック)
- 前回リリース以降に取り込まれた変更を棚卸ししてリリースノートに反映する時
- 既存リリーススクリプト (`scripts/Build-StorePackage.ps1`) の挙動を再確認する時

判断の正当性 (なぜ Store 提出物と GitHub 配布物を分離するか、なぜ `.pfx` を絶対に出さないか) は本ファイルに集約。配布物分離の根拠は SPEC 上の Microsoft Store ingestion 仕様 ("Store は提出された MSIX を再署名する") に由来する。

## 成果物マップ

```
bin/StorePackage/
  ├─ 45014okazuki.SkimDown_<ver>.msixupload          ← Store 提出物 (必ず無署名)
  ├─ 45014okazuki.SkimDown_<ver>.msixbundle          ← 中間成果物 (Store 提出物の中身、無署名)
  ├─ 45014okazuki.SkimDown_<ver>_sideload.msixbundle ← GitHub Releases 添付物 (自己署名)
  ├─ devcert.pfx                                     ← 秘密鍵あり (絶対に公開しない)
  └─ devcert.cer                                     ← 公開鍵のみ (GitHub Releases に添付してよい)
```

| 配布先 | 成果物 | 署名状態 |
|---|---|---|
| Microsoft Store | `*.msixupload` | **無署名** (Store が ingestion で再署名する) |
| GitHub Releases | `*_sideload.msixbundle` + `devcert.cer` | **自己署名** (manifest Publisher と一致する dev cert) |
| 開発者ローカル | `*_sideload.msixbundle` | 同上 |

## バージョン規約

- `Package.appxmanifest` の `Identity/@Version` は **4 セグメント `Major.Minor.Patch.0`**
- **最終セグメントは必ず `0`** — Microsoft Store はリビジョン番号を自身の再署名パイプラインのために予約している (`1.0.2.5` などは弾かれる)
- GitHub の release tag は **3 セグメント `vMajor.Minor.Patch`** (例: `v1.0.2`)
- インクリメント方針:
  - **新機能**: Minor を上げる (`1.0.1.0` → `1.1.0.0`)
  - **バグ修正のみ**: Patch を上げる (`1.0.1.0` → `1.0.2.0`)
  - **破壊的変更**: Major を上げる (Store は同一 `Identity/Name` でアップグレード扱いになるため要注意)

## 手順チェックリスト

### 1. 新機能の棚卸し

前回リリースタグ (`vX.Y.Z`) から HEAD までの変更を確認:

```powershell
git --no-pager log v<prev>..HEAD --oneline
# タグがまだない場合は前回バージョン bump コミットから
git --no-pager log <prev-bump-sha>..HEAD --oneline
```

棚卸し時のチェックリスト:

- [ ] 新規 ADR (`.github/adr/00NN-*.md`) は出ているか → リリースノートにリンク
- [ ] `Package.appxmanifest` 以外で機能差分がコミット済みか
- [ ] 単体テストは緑か (`dotnet test src\SkimDownForWindows.Tests`)
- [ ] `dotnet build SkimDownForWindows.slnx` でビルドエラー / 警告レビュー
- [ ] README に新機能の説明 (使い方 / 制限) が追記済みか

### 2. バージョン更新

`src/SkimDownForWindows/Package.appxmanifest` の `Identity/@Version` を編集:

```diff
- Version="1.0.1.0"
+ Version="1.0.2.0"   # 最終セグメントは 0 のまま
```

他に更新する場所はない (バージョンは manifest 一箇所に集約。`Build-StorePackage.ps1` が manifest から読み取る)。

### 3. パッケージング

```powershell
.\scripts\Build-StorePackage.ps1 -Sign
```

- `-Sign` を付けると Store 用 `.msixupload` (無署名) と **同時に** sideload 用 `_sideload.msixbundle` + `devcert.cer` も生成される
- Store 提出だけなら `-Sign` 不要だが、リリース skill 経由では常に **両方** 生成しておく (GitHub Release も出すため)
- `-SkipClean` は **使わない** (Store 提出ビルドは必ず fresh build)
- `-IncludeX86` は基本不要 (Store ベースラインは x64 + arm64)

### 4. 署名検証

`_sideload.msixbundle` がローカル証明書で正しく署名されているか確認:

```powershell
# Windows SDK 同梱の signtool 経由 (winapp tool でも代替可)
signtool verify /pa /all .\bin\StorePackage\45014okazuki.SkimDown_<ver>_sideload.msixbundle
```

期待: `Successfully verified` と signer の `Subject: CN=57A8C5FA-...` (manifest Publisher と一致)。

無署名側 (`.msixupload`) は **検証する必要はない** (Store ingestion 時に再署名されるため)。

### 5. ローカルインストール検証 (推奨)

可能ならクリーン環境 (別ユーザー / VM / Windows Sandbox) で:

```powershell
# 既存のインストール済みパッケージを除去
Get-AppxPackage 45014okazuki.SkimDown | Remove-AppxPackage

# 公開鍵を TrustedPeople に追加 (管理者 PowerShell)
certutil -addstore -f "TrustedPeople" .\bin\StorePackage\devcert.cer

# インストール
Add-AppxPackage .\bin\StorePackage\45014okazuki.SkimDown_<ver>_sideload.msixbundle

# 起動確認
skimdown <some-folder-with-md>
```

確認項目:

- [ ] 起動する
- [ ] `skimdown` 実行エイリアスが効く
- [ ] 主要機能 (フォルダ open / 多窓 / Theme 切替 / 設定保存) が動く

### 6. GitHub Release ドラフト作成

> **注意 (Copilot CLI 実行時)**: Copilot CLI のデフォルト認証トークンはリポジトリへの読み取り権限しか持たない場合がある (`gh api /repos/<owner>/<repo>` で `permissions.push: false` になる)。その場合 `gh release create` は HTTP 404 で失敗するので、リリース作成は **書き込み権限を持つ人間が手元で実行** すること。エラーメッセージは `"workflow" scope may be required` と表示されることがあるが、実際は repo 書き込み権限の問題なので `gh auth refresh -s workflow` では解決しない。

```powershell
gh release create v<X.Y.Z> `
    --draft `
    --prerelease `
    --target <branch-or-sha> `
    --title "SkimDown for Windows v<X.Y.Z>" `
    --notes-file <path-to-release-notes.md> `
    .\bin\StorePackage\45014okazuki.SkimDown_<ver>_sideload.msixbundle `
    .\bin\StorePackage\devcert.cer
```

- `--draft`: ユーザーが内容確認してから publish する
- `--prerelease`: sideload 版は商用署名版 (Store 経由) と区別するため prerelease 扱い
- 添付物は **`_sideload.msixbundle` と `devcert.cer` の 2 つだけ**

### 7. リリースノートテンプレート

GitHub Releases / Microsoft Store のリリースノート本文は **英語で記載**する。

````markdown
# SkimDown for Windows v<X.Y.Z>

## What's new
- (Feature 1) — Link: `.github/adr/00NN-...md`
- (Feature 2)
- (Bug fix 1)

## Downloads (sideload)
- `45014okazuki.SkimDown_<ver>_sideload.msixbundle` — x64 + ARM64 multi-architecture bundle (self-signed)
- `devcert.cer` — Publisher certificate for the bundle (public key only)

## Install (PowerShell, Run as Administrator)

```powershell
# 1) Trust this Publisher self-signed certificate (one-time setup)
certutil -addstore -f "TrustedPeople" devcert.cer

# 2) Install the package
Add-AppxPackage .\45014okazuki.SkimDown_<ver>_sideload.msixbundle
```

## ⚠️ Important: Migrating to the Microsoft Store build

This GitHub Releases build is a **self-signed sideload build**.
Because the signer and distribution channel differ from the Microsoft Store build,
you **cannot upgrade in place** to the Store build. Uninstall this build first,
then install the Store build.

We strongly recommend the Microsoft Store build for regular users.
(After the Store release is published, add the Store link to this page.)

## Uninstall

```powershell
Get-AppxPackage 45014okazuki.SkimDown | Remove-AppxPackage
# Remove the certificate as well, if needed
certutil -delstore TrustedPeople <thumbprint>
```
````

### 8. Microsoft Store 提出

1. [Partner Center → SkimDown](https://partner.microsoft.com/dashboard/products/9NHTZMM0XMMF/overview) にサインイン
2. 新しい submission を開始 → **Packages**
3. `bin\StorePackage\45014okazuki.SkimDown_<ver>.msixupload` をドラッグドロップ (← **`_sideload` 付きの方ではないことを確認**)
4. ストア掲載情報、年齢区分、スクリーンショットを更新して提出

### 9. リリース後クリーンアップ

- [ ] GitHub Release を draft → published に切り替え (ユーザーの最終承認後)
- [ ] Store 提出が承認されたら、Release notes に Store リンクを追記
- [ ] `bin\StorePackage\devcert.pfx` は安全に削除 (`Remove-Item bin\StorePackage\devcert.pfx`)
  - 同じ証明書を継続利用するなら、リポジトリ外の安全な場所 (Bitwarden / Vault) に退避
- [ ] 次バージョン用の issue / プロジェクトボード更新

### 10. Microsoft Store 提出時のリリースノートを記載

Partner Center の submission 画面で「このバージョンの新機能」欄を更新する。以下のテンプレートをベースに、**英語で**・**ユーザー向けの表現**で埋めて貼り付ける (内部チケット番号や開発者向け略語は避ける)。

```text
SkimDown for Windows v<X.Y.Z>

New features
- <User-visible feature 1>
- <User-visible feature 2>

Improvements and fixes
- <Main improvement or bug fix 1>
- <Main improvement or bug fix 2>

Breaking changes and notes
- <Changes requiring settings migration/reconfiguration or removed behavior>
- <If none, write "None">
```

- バージョン番号は `Package.appxmanifest` の `Major.Minor.Patch` と一致させる
- 本文は英語で記載する
- `New features` と `Breaking changes and notes` は必須で埋める
- 既知の制限や回避方法があれば `Breaking changes and notes` に追記する

## やってはいけないこと

1. **`.msixupload` を GitHub Releases に「インストール用」として添付**
    - `.msixupload` は無署名なので `Add-AppxPackage` できない
    - 透明性目的で添付する場合でも、`_sideload.msixbundle` と並べて誤認させないようリリースノートで区別する (基本は添付しない方針)
2. **`devcert.pfx` を GitHub Releases / PR / Issue / artifact に上げる**
    - 秘密鍵を漏らすと、信頼ストアにこの cert を入れた全てのユーザーに対して同じ Publisher の悪意ある MSIX を署名可能になる
    - `.gitignore` 既存ルール (`*.pfx` 系) に頼らず、`bin\` 配下を **絶対にコミットしない**
3. **`Identity/@Version` の最終セグメントを `0` 以外に**
    - Store ingestion で reject される
4. **同じバージョン番号で再ビルドして Store に提出**
    - Store は同一 `Identity/Version` を受け付けない。バージョンは必ず単調増加
5. **`Build-StorePackage.ps1 -SkipClean` でリリースビルド**
    - 前回ビルドの残骸が紛れ込むリスク
6. **`-Sign` 時に `winapp sign` を直接 `.msixbundle` (無署名側) に当てる**
    - スクリプト改修時の事故。`.msixupload` が自己署名版を含んでしまい、Store の意図と齟齬
    - **無署名 bundle と sideload bundle はファイル名で分離** (`_sideload` サフィックス)
7. **`Trusted Root Certification Authorities` への dev cert インストールを推奨**
    - 過剰な信頼付与。`TrustedPeople` で十分
8. **GitHub Release に Store 版への移行リスクを書かない**
    - Publisher が異なるため上書き不可になる事実をユーザーに伝えないと、サポート負荷が出る

## トラブルシューティング

### `dotnet publish` が失敗する (1 アーキ目だけ通る)

`Build-StorePackage.ps1` は各アーキで `dotnet publish` を直列に実行する。失敗時はそのアーキの publish ログを確認。よくある原因:

- `winapp` CLI が PATH にない → `winget install Microsoft.WinAppCLI`
- WinAppSDK / .NET 10 SDK 未インストール → README "Prerequisites" 参照
- 既存 `bin/obj` がロックされている → 別 Visual Studio / VS Code を閉じる

### `winapp package` でマニフェスト関連エラー

`AppxManifest.xml` がトークン置換 (`$targetnametoken$`) 残っている場合。スクリプトには fallback 経路がある (build 出力からコピー) が、両方失敗する時は `dotnet publish` 出力ディレクトリを手動確認。

### `signtool verify` で `SignerHash mismatch`

`-Sign` 実行時に既存の `devcert.pfx` を使い回しているが manifest Publisher が変わった場合。`devcert.pfx` を削除して再生成 (`Build-StorePackage.ps1 -Sign` で再生成される)。

### `Add-AppxPackage` が `0x800B0109 (TRUST_E_NOSIGNATURE)` で失敗

`devcert.cer` を `TrustedPeople` に入れ忘れているか、間違ったストアに入れている。管理者 PowerShell で:

```powershell
certutil -addstore -f "TrustedPeople" devcert.cer
```

### `Add-AppxPackage` が `0x80073CF3 (Package Family Conflict)` で失敗

既に異なる署名者で同じ `Identity/Name` がインストールされている。`Get-AppxPackage 45014okazuki.SkimDown | Remove-AppxPackage` で削除してから再インストール。
**これは Store 版 ↔ sideload 版の移行時にも起きる**。

## 関連

- README "Microsoft Store submission" セクション
- `scripts/Build-StorePackage.ps1` (本 skill が前提とするビルドスクリプト)
- `.github/adr/` (リリースで紹介する機能の根拠)
