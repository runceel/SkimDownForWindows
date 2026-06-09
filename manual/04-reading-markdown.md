# 4. Reading Markdown

SkimDown renders rich Markdown with a bundled engine (markdown‑it + highlight.js + KaTeX + Mermaid
+ emoji), so everything works **fully offline** — no CDN, no internet. This page tours what gets
rendered.

## Text formatting and color swatches

Standard inline formatting is supported — **bold**, *italic*, ~~strikethrough~~, `inline code`,
and links. When SkimDown recognizes a color value (like `#2563eb`), it shows a small **color
swatch** next to it.

## Code blocks with syntax highlighting

Fenced code blocks are syntax‑highlighted, with the language shown in the corner.

![A Python code block with syntax highlighting](images/code.png)

## Tables and task lists

GitHub‑flavored tables and task lists (checkboxes) render cleanly, and emoji shortcodes are turned
into emoji.

![A rendered table and a task list with checkboxes](images/tables-tasks.png)

## GitHub‑style alerts

Blockquote alerts — **Note**, **Tip**, **Important**, **Warning**, and **Caution** — are styled
with their familiar colors and icons.

![The five GitHub-style alert callouts](images/alerts.png)

## Math with KaTeX

Inline math like `$E = mc^2$` and display equations (`$$ … $$`) are typeset with **KaTeX**.

![Rendered inline and block math equations](images/math-katex.png)

## Diagrams with Mermaid

Fenced ` ```mermaid ` blocks are rendered as diagrams — flowcharts, sequence diagrams, and more.

![A rendered Mermaid flowchart](images/mermaid-flowchart.png)

> **Tip:** Click a Mermaid diagram to open it in a larger, zoomable view — handy for big graphs.

## Reading comfortably

- **Zoom** the whole preview in or out, and set how **wide** the text column is — see
  [View & appearance](07-view-and-appearance.md).
- **Find** text within the current file with **Ctrl+F** — see
  [Searching & copying](06-search-and-copy.md).
