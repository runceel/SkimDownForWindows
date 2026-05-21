# WinUI plugin (vendored)

This is a verbatim copy of the **`winui`** plugin from
[microsoft/win-dev-skills](https://github.com/microsoft/win-dev-skills),
distributed via the `awesome-copilot` plugin marketplace. It is committed to
this repository so that anyone cloning `SkimDownForWindows` has the same
skills available when working with GitHub Copilot CLI — without first having
to install the marketplace plugin.

## What's inside

```
winui/
├── plugin.json                          (marketplace metadata)
├── agents/
│   └── winui-dev.agent.md               (interactive WinUI dev agent)
└── skills/
    ├── winui-code-review/               (MVVM / x:Bind / a11y / theme review)
    ├── winui-design/                    (Fluent design, colors, typography)
    ├── winui-dev-workflow/              (BuildAndRun.ps1 + project setup)
    ├── winui-packaging/                 (MSIX / sourcegen patterns)
    ├── winui-session-report/            (session diagnostics)
    ├── winui-setup/                     (one-time prereq install)
    ├── winui-ui-testing/                (automated UI testing recipes)
    └── winui-wpf-migration/             (WPF → WinUI 3 migration)
```

## How this was used here

The original `SkimDownForWindows` MVP was built with the **`winui-dev-workflow`**
skill (the `BuildAndRun.ps1` script in particular handles platform detection,
MSIX packaging registration, and `winapp run` launching with debug output).

## Updating

To pull a newer version of the upstream plugin into this repo:

```powershell
# After `gh copilot extension install awesome-copilot/winui` or similar,
# the plugin lands in:
$src = "$env:USERPROFILE\.copilot\installed-plugins\awesome-copilot\winui"
$dst = "$PSScriptRoot\winui"
Copy-Item -Path $src -Destination $dst -Recurse -Force
```

Or use `gh repo sync` / re-vendor by hand.

## Upstream

- Plugin metadata: `winui/plugin.json` (`"name": "winui"`, `"version": "0.3.1"`)
- Source repo: https://github.com/microsoft/win-dev-skills
- License: MIT (see the upstream repo for full text)
