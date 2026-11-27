# SlimDep for Visual Studio Code

Find and remove unused NuGet package references without leaving VS Code. Results appear in a tree view in the Explorer and as CodeLens on `<PackageReference>` lines in `.csproj` files.

The extension is a front end for the [SlimDep CLI](https://www.nuget.org/packages/SlimDep), which does all the analysis. Install the CLI first:

```bash
dotnet tool install --global SlimDep     # 1.2.1 or later recommended
```

## Quick start

1. Open a folder containing `.csproj` files. The extension activates automatically.
2. Run `dotnet restore`. SlimDep reads the restore output; no build is needed.
3. Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`) → **SlimDep: Scan for Unused Packages**.
4. Review the **Unused NuGet Packages** panel in the Explorer.
5. Run **SlimDep: Remove All Unused Packages**, confirm, then `dotnet restore` again.

## Commands

| Command | Where | What it does |
|---|---|---|
| **SlimDep: Scan for Unused Packages** (`slimdep.scan`) | Command Palette | Runs `slimdep scan <project> --format json` for every `.csproj` in the workspace (excluding `obj/`). Shows progress in the status bar and fills the tree view. |
| **SlimDep: Refresh** (`slimdep.refresh`) | Panel title bar ↻, Command Palette | Same as Scan. |
| **SlimDep: Remove All Unused Packages** (`slimdep.fix`) | Panel title bar 🗑, CodeLens, Command Palette | Asks for confirmation, runs `slimdep fix` for each project with removable findings, then drops the removed packages from the tree. Run `dotnet restore` before scanning again. |
| **SlimDep: Ignore this package** (`slimdep.ignorePackage`) | Inline 🔇 on a package in the tree | Runs `slimdep ignore add`, then re-scans. |

What the tree shows:
```
UNUSED NUGET PACKAGES
└── MyApp.Api.csproj        2 unused, 1 transitive
    ├── Newtonsoft.Json     13.0.3
    ├── Serilog.Sinks.File  5.0.0
    └── ⓘ System.Memory     4.5.5 (transitive)
```

How each feature behaves:
- **Tree items:** hover a package to see why it was reported.
- **Transitive packages:** only shown with `slimdep.includeTransitive`. They're marked ⓘ, are informational, and are never offered for removal. They stay in the tree after Remove, because the CLI can't remove them from the project file.
- **After Remove:** run `dotnet restore` before the next scan. Until then, `project.assets.json` still lists the removed references, and a scan would report them again.
- **CodeLens:** `⚠ Unused — run slimdep fix to remove` appears above removable packages, and only in the `.csproj` they belong to. Clicking it opens the same confirmation as **Remove All Unused Packages**.
- **Ignore:** the package is added to the `.slimdepignore` SlimDep actually reads for that project: the nearest one found by searching upward from the project. If there isn't one, it's created in the project's workspace folder, which handles multi-root workspaces correctly.
- **Errors:**
  - If the CLI isn't installed, you get an error with an **Open Terminal** button.
  - Projects that fail to scan (e.g. not restored) are summarised in one warning, and the other projects still appear.
  - Each project scan times out after 60 seconds.

## Settings

| Setting | Default | Description |
|---|---|---|
| `slimdep.toolPath` | `slimdep` | Path to the `slimdep` executable, if it isn't on `PATH`. |
| `slimdep.autoScanOnOpen` | `false` | Scan automatically when a workspace containing a `.csproj` is opened. |
| `slimdep.includeTransitive` | `false` | Also list unused transitive packages (`--include-transitive`). |

## What gets detected

The extension shows the CLI's SLIMDEP001 results: direct references whose namespaces your code never uses. The CLI understands:
- C# (`using`, fully-qualified names, every `#if` branch)
- SDK implicit usings and `<Using>` items
- XAML (`clr-namespace:`/`using:`, `.axaml`)
- Razor (`@using`, `@inject`, `@model`, `@code`, …)
- linked and shared-project source files
- multi-targeting

The CLI never reports these as unused, so they never appear in the tree:
- version pins (references that raise a transitive dependency's version)
- built-in safe-list packages such as test adapters, coverage and SourceLink
- `PrivateAssets="all"` references
- packages listed in `.slimdepignore`

Full rules: [SlimDep README](https://github.com/vinaydiddi/SlimDep#readme).

## Limitations

- The panel doesn't yet show SLIMDEP002 (transitive packages used directly), version pins or safe-list skips. Run `slimdep scan` in a terminal to see them.
- **Remove All** removes every listed package in the affected projects; you can't pick individual packages. To keep one, use **Ignore** first.
- Analysis limitations are the CLI's: namespace-based matching, MSBuild conditions not evaluated, C# projects only. See the [README](https://github.com/vinaydiddi/SlimDep#limitations).

## License

MIT © 2026 Vinay Diddi
