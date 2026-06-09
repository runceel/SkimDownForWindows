# 2. Opening content

SkimDown gives you several ways to open a folder of Markdown — pick whichever fits your workflow.

## Open a folder

Use any of these:

- **File → Open Folder…**
- **Ctrl+O**
- **Drag a folder** from File Explorer onto the SkimDown window.
- **Command line:** `skimdown D:\notes` (or `skimdown .` to open the current folder in any
  terminal).

![The File menu showing Open Folder, Open Recent, Reveal in File Explorer, Copy File Path, and Close Window](images/menu-file.png)

The **File** menu also offers:

| Item | What it does |
| --- | --- |
| **Open Folder…** (Ctrl+O) | Choose a folder to read. |
| **Open Recent** ▸ | Reopen a folder you viewed recently. |
| **Reveal in File Explorer** | Show the current file or folder in Explorer. |
| **Copy File Path** | Copy the selected file's full path to the clipboard. |
| **Close Window** (Ctrl+W) | Close the current window. |

### Open a recent folder

**File → Open Recent** lists folders you opened before, so you can jump back without browsing.
SkimDown remembers your recent folders between sessions. (Opening a single `.md` file does **not**
add it to this list — see [Single‑file mode](05-single-file-mode.md).)

## Multiple windows

You can read several folders at once, each in its own window.

![The Window menu with New Window, Minimize, Zoom, Bring All to Front, and the open window list](images/menu-window.png)

- **Window → New Window** (**Ctrl+N**) opens an additional, empty window.
- Dropping a folder onto a window that **already** has a folder opens the dropped folder in a
  **new** window, leaving your current one untouched.
- The **Window** menu lists all open windows at the bottom; the active one is checked. Use
  **Bring All to Front** to gather them, or **Minimize** (**Ctrl+M**) / **Zoom** to manage the
  current one.

## Opening a single file

To open just one Markdown file (instead of a whole folder), see
[Single‑file mode](05-single-file-mode.md). In short: double‑click a `.md` file in Explorer
(**Open with → SkimDown**), run `skimdown README.md`, or drag a single `.md` file onto the window.
