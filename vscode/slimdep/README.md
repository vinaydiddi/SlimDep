# SlimDep for Visual Studio Code

Detect and remove unused NuGet package references directly inside VS Code — with a **tree view panel** and **inline CodeLens** on every `.csproj` file.

---

## Requirement

The SlimDep CLI must be installed as a .NET global tool:

```bash
dotnet tool install --global SlimDep
```

> The extension shells out to `slimdep` — it will not work without the CLI installed.

---

## Features

### Unused Packages Tree View
A dedicated **"Unused NuGet Packages"** panel appears in the Explorer sidebar whenever a `.csproj` file is detected in your workspace. It lists every unused package grouped by project.

### Inline CodeLens on `.csproj` files
Open any `.csproj` file and unused packages are highlighted with a CodeLens annotation directly above the `<PackageReference>` element.

### Commands

| Command | Description |
|---|---|
| `SlimDep: Scan for Unused Packages` | Scan all `.csproj` files in the workspace |
| `SlimDep: Remove All Unused Packages` | Auto-remove all unused packages from all projects |
| `SlimDep: Refresh` | Re-run the scan and refresh the tree view |
| `SlimDep: Ignore this package` | Add a package to `.slimdepignore` (right-click a tree item) |

Run any command via the **Command Palette** (`Ctrl+Shift+P` / `Cmd+Shift+P`).

---

## Usage

1. Open a .NET workspace that contains `.csproj` files
2. Run `dotnet restore` in the terminal (required before scanning)
3. Open the Command Palette → **SlimDep: Scan for Unused Packages**
4. Review unused packages in the **"Unused NuGet Packages"** panel in the Explorer
5. Click **SlimDep: Remove All Unused Packages** to auto-remove them
6. Run `dotnet restore` again after removal

---

## Commands In Detail

### SlimDep: Scan for Unused Packages
**Command ID:** `slimdep.scan`

Scans all `.csproj` files found in the current workspace for unused NuGet package references.

**How to run:**
- Command Palette (`Ctrl+Shift+P`) → type `SlimDep: Scan`
- Click the **Refresh** button (↻) in the **Unused NuGet Packages** panel title bar

**What happens:**
- A progress indicator appears in the status bar while scanning
- Each `.csproj` is scanned individually via the `slimdep` CLI
- Results populate the **Unused NuGet Packages** tree view in the Explorer
- Unused packages are also annotated inline with `⚠ Unused` CodeLens above their `<PackageReference>` line in the `.csproj` editor

**Example — tree view result after scan:**
```
UNUSED NUGET PACKAGES
└── MyApp.Api.csproj  (2 unused)
    ├── Newtonsoft.Json  13.0.3
    └── Serilog.Sinks.File  5.0.0
└── MyApp.Core.csproj  (1 unused)
    └── CsvHelper  30.0.1
```

**Example — CodeLens in `.csproj` editor:**
```xml
⚠ Unused — run slimdep fix to remove
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
```

---

### SlimDep: Remove All Unused Packages
**Command ID:** `slimdep.fix`

Removes all unused packages found in the last scan from their respective `.csproj` files.

**How to run:**
- Command Palette (`Ctrl+Shift+P`) → type `SlimDep: Remove`
- Click the **trash** icon (🗑) in the **Unused NuGet Packages** panel title bar
- Click the `⚠ Unused — run slimdep fix to remove` CodeLens annotation in the `.csproj` editor

**What happens:**
1. A confirmation dialog appears showing how many packages will be removed and from how many projects
2. After confirming, the `slimdep fix` CLI command runs for each affected project
3. The tree view and CodeLens annotations are cleared
4. A notification reminds you to run `dotnet restore`

**Example confirmation dialog:**
```
SlimDep: Remove 3 unused package(s) from 2 project(s)?
                                          [Remove]  [Cancel]
```

> ⚠ This writes changes to your `.csproj` files. Always review the scan results before confirming.

---

### SlimDep: Refresh
**Command ID:** `slimdep.refresh`

Re-runs the scan and refreshes the tree view with the latest results. Identical to **Scan** but accessible directly from the panel title bar.

**How to run:**
- Click the **↻** icon in the **Unused NuGet Packages** panel title bar
- Command Palette (`Ctrl+Shift+P`) → type `SlimDep: Refresh`

---

### SlimDep: Ignore this package
**Command ID:** `slimdep.ignorePackage`

Adds a package to `.slimdepignore` in the workspace root so it is permanently excluded from future scans.

**How to run:**
- **Right-click** any package in the **Unused NuGet Packages** tree view → **SlimDep: Ignore this package**

**What happens:**
1. Calls `slimdep ignore add <packageName> --path <workspaceRoot>`
2. Creates or updates `.slimdepignore` in the workspace root
3. Automatically re-scans so the package disappears from the tree view

> Use this for packages that are used via reflection, DI registration, or other non-`using`-directive patterns that SlimDep cannot detect.

---

## Typical Workflow

```
1. dotnet restore              ← must run before scanning
2. Scan (Ctrl+Shift+P)        ← finds unused packages
3. Review tree view            ← check what would be removed
4. Remove (confirm dialog)    ← writes changes to .csproj files
5. dotnet restore              ← update the lockfile after removal
```

---

## Settings

| Setting | Default | Description |
|---|---|---|
| `slimdep.toolPath` | `slimdep` | Path to the `slimdep` executable. Override if installed to a custom location. |
| `slimdep.autoScanOnOpen` | `false` | Automatically scan when a `.csproj` file is opened in the editor. |
| `slimdep.includeTransitive` | `false` | Also scan transitive (indirect) packages (`--include-transitive`). |

Configure in **File → Preferences → Settings** → search for `SlimDep`.

---

## How It Works

The extension delegates all analysis to the `slimdep` CLI tool, which:

- Reads `obj/project.assets.json` to discover package DLLs
- Uses **Roslyn** to parse `.cs` files and extract `using` directives and **fully-qualified name usages**
- Parses `xmlns clr-namespace:` declarations in **XAML** files (WPF, MAUI)
- Parses `@using` directives in **Razor / CSHTML** files (Blazor, MVC)
- Uses **.NET reflection metadata** to extract namespaces exported by each package
- Flags any package whose namespaces never appear in any source file
- Respects `.slimdepignore` files to suppress false positives
- Detects **Central Package Management** (CPM) `GlobalPackageReference` entries and excludes them from per-project analysis

No build is required — only `dotnet restore`.

---

## Supported Features

| Feature | Status |
|---|---|
| Scan a single `.csproj` file | ✅ |
| Scan a folder recursively (finds all `.csproj` files) | ✅ |
| Detect unused direct package references | ✅ |
| Detect unused transitive packages | ✅ |
| Fully-qualified name detection (`Foo.Bar.Type` without `using` directive) | ✅ |
| XAML `clr-namespace` scanning (WPF, MAUI) | ✅ |
| Razor / CSHTML `@using` scanning (Blazor, MVC) | ✅ |
| `.slimdepignore` file support (suppress false positives) | ✅ |
| Central Package Management (CPM) GlobalPackageReference detection | ✅ |
| Skip build tools and source generators (no exported namespaces) | ✅ |
| Parallel multi-project scanning | ✅ |
| Inline CodeLens on `.csproj` files | ✅ |
| Tree view panel with project/package hierarchy | ✅ |
| Confirmation dialog before removing packages | ✅ |
| Right-click "Ignore" to add to `.slimdepignore` from tree view | ✅ |
| `includeTransitive` setting to scan indirect packages | ✅ |
| CLI not installed — error notification with Open Terminal button | ✅ |
| Auto-scan on workspace open (optional, off by default) | ✅ |

---

## Known Limitations

- Packages used only in XAML `x:Type` or code-behind without a `using` or `clr-namespace` declaration may be reported as unused
- Does not analyse `_ViewImports.cshtml` automatically (add `@using` directives there to resolve)
- Requires `obj/project.assets.json` — run `dotnet restore` before scanning

---

## CLI Tool

SlimDep is also available as a standalone CLI tool for terminal and CI pipeline use:

```bash
slimdep scan C:\Dev\MySolution
slimdep fix C:\Dev\MySolution --dry-run
slimdep ignore add Microsoft.EntityFrameworkCore.Design
```

Install: [`dotnet tool install --global SlimDep`](https://www.nuget.org/packages/SlimDep)

---

## License

MIT © 2026 Vinay Diddi
