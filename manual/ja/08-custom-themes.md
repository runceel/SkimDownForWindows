# 8. カスタムカラースキーム

**System / Light / Dark** に加えて、SkimDown はプレビュー領域用の独自テーマを読み込めます。テーマは [VS Code のカラーテーマ形式](https://code.visualstudio.com/api/references/theme-color) を使うため、既存の VS Code テーマファイルを再利用できます。

## テーマの置き場所

`*.json` のテーマファイルを、ローカルアプリデータ配下の Themes フォルダーに置きます。

- **パッケージ版（Store / サイドロード）ビルド:**
  `%LOCALAPPDATA%\Packages\<package-family>\LocalState\Themes\`
- **アンパッケージ版ビルド:** `%LOCALAPPDATA%\SkimDownForWindows\Themes\`

最も手早くたどり着く方法は **View → Theme → Open Themes Folder**（表示 → テーマ → テーマフォルダーを開く）です。

![Open Themes Folder と Reload Themes を含む Theme サブメニュー](../images/submenu-theme.png)

ファイルを追加・編集した後は、**View → Theme → Reload Themes**（表示 → テーマ → テーマを再読み込み）を選んで一覧を更新します。フォルダーは自動では監視され**ません**。

## テーマファイルの形式

各ファイルは独立した VS Code のカラーテーマです。

```json
{
  "$schema": "vscode://schemas/color-theme",
  "name": "My Theme",
  "type": "dark",
  "colors": {
    "editor.background": "#1e1e1e",
    "editor.foreground": "#d4d4d4",
    "textLink.foreground": "#3794ff"
  }
}
```

- **`name`** — **View → Theme** に表示されるラベル。省略した場合はファイル名になります。
- **`type`** — `"light"` または `"dark"`。ライト/ダークのコードハイライト用パレットと、Mermaid 図のテーマを選びます。
- **`colors`** — VS Code のカラーキー。SkimDown は以下に挙げるサブセットを読み取り、それ以外のキーは無視します。

> シンタックスハイライトの `tokenColors` はまだ**サポートされていません**。コードブロックは `type` に応じて GitHub のライトまたはダークのパレットを使います。

## どの色が使われるか

SkimDown は一部の VS Code キーを、自身のプレビュー用変数に対応付けます。各行について、最初に見つかったキーを使います。キーが欠けている場合は、そのテーマの `type` に応じた組み込みパレットにフォールバックします。

| プレビュー要素 | VS Code キー（優先順） |
| --- | --- |
| 背景 | `editor.background` |
| 前景テキスト | `editor.foreground`、`foreground` |
| 控えめなテキスト | `descriptionForeground`、`disabledForeground` |
| 罫線 | `panel.border`、`editorGroup.border`、`editorWidget.border`、`contrastBorder` |
| 柔らかい面 | `editorGroupHeader.tabsBackground`、`editor.lineHighlightBackground`、`sideBar.background` |
| 強い面 | `editorWidget.background`、`editor.background` |
| コード背景 | `editor.lineHighlightBackground`、`editorGroupHeader.tabsBackground` |
| 表のストライプ | `editorGroupHeader.tabsBackground`、`editor.lineHighlightBackground` |
| リンク | `textLink.foreground`、`editorLink.activeForeground`、`focusBorder` |
| 引用ブロック | `descriptionForeground`、`editor.foreground` |
| 検索ハイライト | `editor.findMatchHighlightBackground` |
| 現在の一致 | `editor.findMatchBackground` |

色の値は安全な CSS 形式でなければなりません: `#rgb`、`#rgba`、`#rrggbb`、`#rrggbbaa`、`rgb(...)`、`rgba(...)`、`hsl(...)`、`hsla(...)`、または `transparent`。`var()`、`calc()`、`url()`、`;`、`{`、`}` を含む値は拒否され、組み込みパレットにフォールバックします。

## 選択と切り替え

**View → Theme**（表示 → テーマ）を開くと、組み込みテーマ、登録済みのカスタムテーマ、そして **Open Themes Folder** / **Reload Themes** の操作が表示されます。現在のテーマにはチェックが付きます。選択中のカスタムテーマの元となる JSON ファイルが削除された場合、SkimDown は次回起動時、または **Reload Themes** を選んだときに、**System** へフォールバックします。
