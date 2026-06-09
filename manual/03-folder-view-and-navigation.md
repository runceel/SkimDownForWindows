# 3. The folder view & navigation

Once a folder is open, SkimDown shows two areas: the **sidebar tree** on one side and the
**preview** on the other.

![SkimDown window anatomy: title bar, menu bar, sidebar tree, and preview](images/main-window.png)

## Window anatomy

1. **Title bar** — shows the folder name and the app name, e.g. `SkimDown Demo — SkimDown`.
2. **Menu bar** — **File**, **Edit**, **View**, **Window**, **Help**.
3. **Sidebar header** — the folder name, a count such as **6 markdown files**, and the
   **view‑mode toggle** (tree or date‑sorted list).
4. **Sidebar tree** — every Markdown file in the folder, organized by subfolder.
5. **Preview** — the rendered Markdown of the selected file.

## The sidebar tree

SkimDown scans the folder **recursively** for `.md` and `.markdown` files and shows them
folder‑first, sorted case‑insensitively. Files inside subfolders (like `Getting Started`,
`Guides`, and `Reference` above) are grouped under their folders.

Hidden files and noise directories are filtered out automatically — folders such as `.git`,
`node_modules`, `.build`, and `DerivedData` are never shown in the tree.

### Navigating

- **Click a file** to render it in the preview.
- **Click a folder** (or its chevron) to expand or collapse it.
- Press **Ctrl+B** to hide or show the sidebar entirely (**View → Toggle Sidebar**).

SkimDown **remembers your expansion state per folder**, as well as the last file you had selected,
so reopening a folder restores your previous state.

## View modes

The two buttons in the **sidebar header** switch how the file list is organized:

- **Tree view** — the default. Markdown files are grouped by subfolder and sorted
  case‑insensitively, folder‑first (as described above).
- **Sort by date modified** — a single **flat list** of every Markdown file in the folder,
  **newest first**. Subfolder grouping is ignored; instead each row shows the file name with its
  **last‑modified date and time** and the **subfolder it lives in** just below (blank for files at
  the top level). This makes it easy to jump straight to whatever changed most recently.

Long file names and folder paths that don't fit the sidebar width are shortened with an ellipsis
(`…`); hover over a row to see the full text in a tooltip.

Your choice is a single **app‑wide preference** — it applies to every folder you open and is
remembered between sessions, so the next folder (and the next launch) opens in the same mode. The
toggle is unavailable in [single‑file mode](05-single-file-mode.md), which shows just one file with
no list.

## Live updates

The tree and preview stay in sync with disk. If Markdown files are **added, deleted, renamed, or
updated** while a folder is open, SkimDown refreshes automatically — no manual reload needed.

## Moving or hiding the sidebar

You can put the sidebar on the **left** or the **right**, or hide it completely. See
[View & appearance](07-view-and-appearance.md#sidebar).
