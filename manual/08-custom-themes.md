# 8. Custom color schemes

Beyond **System / Light / Dark**, SkimDown can load your own themes for the preview area. Themes
use the [VS Code color theme format](https://code.visualstudio.com/api/references/theme-color), so
you can reuse existing VS Code theme files.

## Where themes live

Drop `*.json` theme files into the Themes folder under your local app data:

- **Packaged (Store / sideload) build:**
  `%LOCALAPPDATA%\Packages\<package-family>\LocalState\Themes\`
- **Unpackaged build:** `%LOCALAPPDATA%\SkimDownForWindows\Themes\`

The quickest way to get there is **View → Theme → Open Themes Folder**.

![The Theme submenu, including Open Themes Folder and Reload Themes](images/submenu-theme.png)

After adding or editing a file, choose **View → Theme → Reload Themes** to refresh the list — the
folder is **not** watched automatically.

## Theme file format

Each file is a stand‑alone VS Code color theme:

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

- **`name`** — the label shown in **View → Theme**. Falls back to the file name if omitted.
- **`type`** — `"light"` or `"dark"`. Picks the light/dark code‑highlight palette and the Mermaid
  diagram theme.
- **`colors`** — VS Code color keys. SkimDown reads the subset listed below; other keys are
  ignored.

> Syntax‑highlighting `tokenColors` are **not** supported yet. Code blocks use GitHub's light or
> dark palette based on `type`.

## Which colors are used

SkimDown maps a handful of VS Code keys onto its own preview variables. For each row it uses the
first key it finds; missing keys fall back to the built‑in palette for the theme's `type`.

| Preview element | VS Code keys (in priority order) |
| --- | --- |
| Background | `editor.background` |
| Foreground text | `editor.foreground`, `foreground` |
| Muted text | `descriptionForeground`, `disabledForeground` |
| Borders | `panel.border`, `editorGroup.border`, `editorWidget.border`, `contrastBorder` |
| Soft surfaces | `editorGroupHeader.tabsBackground`, `editor.lineHighlightBackground`, `sideBar.background` |
| Stronger surfaces | `editorWidget.background`, `editor.background` |
| Code background | `editor.lineHighlightBackground`, `editorGroupHeader.tabsBackground` |
| Table stripe | `editorGroupHeader.tabsBackground`, `editor.lineHighlightBackground` |
| Links | `textLink.foreground`, `editorLink.activeForeground`, `focusBorder` |
| Blockquote | `descriptionForeground`, `editor.foreground` |
| Search highlight | `editor.findMatchHighlightBackground` |
| Current match | `editor.findMatchBackground` |

Color values must be a safe CSS form: `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, `rgb(...)`,
`rgba(...)`, `hsl(...)`, `hsla(...)`, or `transparent`. Values containing `var()`, `calc()`,
`url()`, `;`, `{`, or `}` are rejected and fall back to the built‑in palette.

## Selecting and switching

Open **View → Theme** to see the built‑ins, your registered custom themes, and the **Open Themes
Folder** / **Reload Themes** actions. The active theme is checked. If the JSON file behind a
selected custom theme is deleted, SkimDown falls back to **System** the next time it starts
or when you choose **Reload Themes**.
