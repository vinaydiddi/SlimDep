# SlimDep — Usage Guide

**Version:** `1.1.1` &nbsp;|&nbsp; **Runtime:** .NET 8, 9, or 10 &nbsp;|&nbsp; **License:** MIT

SlimDep detects and removes unused NuGet package references from `.NET` projects.

---

## Installation

### As a .NET Global Tool (recommended)

```bash
dotnet tool install --global SlimDep
```

Once installed, the `slimdep` command is available everywhere on your machine.

### Update

```bash
dotnet tool update --global SlimDep
```

### Uninstall

```bash
dotnet tool uninstall --global SlimDep
```

---

## Before You Scan

Your project must be restored so that `obj/project.assets.json` exists:

```bash
dotnet restore
```

> **Tip:** SlimDep does not require a build — only a restore.

---

## Supported Features

| Feature | Notes |
|---|---|
| Scan a single `.csproj` | Pass the file path directly |
| Scan a folder recursively | Finds all `.csproj` files, skips `obj/` automatically |
| Console output | Human-readable with ✓/✗ indicators and timing |
| JSON output | Machine-readable, great for CI dashboards |
| SARIF 2.1.0 output | GitHub Code Scanning / Azure DevOps inline PR annotations |
| Write report to file | `--output report.json` or `--output results.sarif` |
| Detect unused direct references | Core feature |
| Report unused transitive references | Enable with `--include-transitive`; informational only, never removed |
| Fully-qualified names | `Foo.Bar.Type` without a `using` is detected |
| XAML and Razor | `clr-namespace` (WPF/MAUI) and `@using` (Blazor/MVC) are scanned |
| Multi-targeting | All target frameworks are analysed and `#if` blocks are included |
| Project-level usings | SDK implicit usings, `<Using>` items and generated `GlobalUsings.g.cs` are honoured |
| Ignore list | `.slimdepignore`, `--ignore`, and `slimdep ignore add/remove/list` |
| SARIF output | `--format sarif` for GitHub Code Scanning / Azure DevOps |
| Skip build-tool-only packages | Packages with no exported namespaces are never flagged |
| Auto-remove from `.csproj` | `fix` command edits the file in-place |
| Dry-run preview | `--dry-run` shows what would be removed without writing |
| CI-friendly exit codes | `0` = clean, `1` = unused found, `2` = error |
| Multi-project fix | One command fixes all projects in a folder |

---

## Commands

### `slimdep scan` — Detect unused packages

```
slimdep scan <project> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<project>` | Path to a `.csproj` file **or** a folder (all `.csproj` files found recursively) |

**Options**

| Option | Default | Description |
|---|---|---|
| `--format` | `console` | Output format: `console`, `json`, or `sarif` |
| `--output <file>` | _(stdout)_ | Write the report to a file instead of printing to the terminal |
| `--include-transitive` | off | Also report unused transitive (indirect) dependencies |
| `--ignore <pkg>` | _(none)_ | Suppress a package from analysis (repeatable) |

With `--format json` or `--format sarif` and no `--output`, stdout contains only the report, so it can be piped. Scanning several projects as JSON produces one array.

**Exit codes**

| Code | Meaning |
|---|---|
| `0` | All packages are in use |
| `1` | One or more unused packages found |
| `2` | Error (project not found, not restored, etc.) |

**Examples**

```bash
# Scan a single project
slimdep scan MyApp.csproj

# Scan an entire solution folder (finds all .csproj recursively)
slimdep scan C:\Dev\MySolution

# Include indirect/transitive dependencies
slimdep scan MyApp.csproj --include-transitive

# Output as JSON
slimdep scan MyApp.csproj --format json

# Save JSON report to a file
slimdep scan MyApp.csproj --format json --output report.json

# SARIF output — for GitHub Code Scanning / Azure DevOps inline annotations
slimdep scan MyApp.csproj --format sarif --output results.sarif

# Use in CI — fails the pipeline if unused packages are found
slimdep scan MyApp.csproj
```

**Sample console output**

```
Found 3 project(s).
Scanning MyApp.Api.csproj ...

  Project : C:\Dev\MySolution\src\MyApp.Api\MyApp.Api.csproj
  Packages: 8 direct reference(s) analysed
  Duration: 142 ms

  ✗ 2 unused package(s) found:

    • Newtonsoft.Json 13.0.3
      No namespace from this package appears in any using directive
    • Serilog.Sinks.File 5.0.0
      No namespace from this package appears in any using directive

  Run 'slimdep fix <project>' to remove them automatically.

Scanning MyApp.Core.csproj ...

  Project : C:\Dev\MySolution\src\MyApp.Core\MyApp.Core.csproj
  Packages: 5 direct reference(s) analysed
  Duration: 89 ms

  ✓ All packages are used. Nothing to remove.
```

**Sample JSON output** (`--format json`)

```json
{
  "projectPath": "C:\\Dev\\MySolution\\src\\MyApp.Api\\MyApp.Api.csproj",
  "totalDirectPackages": 8,
  "unusedCount": 2,
  "durationMs": 142.35,
  "unusedPackages": [
    {
      "name": "Newtonsoft.Json",
      "version": "13.0.3",
      "reason": "No namespace from this package appears in any using directive"
    },
    {
      "name": "Serilog.Sinks.File",
      "version": "5.0.0",
      "reason": "No namespace from this package appears in any using directive"
    }
  ]
}
```

---

### `slimdep fix` — Remove unused packages

```
slimdep fix <project> [options]
```

Removes the unused `<PackageReference>` entries directly from the `.csproj` file(s). Always run `--dry-run` first to preview changes.

**Arguments**

| Argument | Description |
|---|---|
| `<project>` | Path to a `.csproj` file **or** a folder (all `.csproj` files found recursively) |

**Options**

| Option | Default | Description |
|---|---|---|
| `--dry-run` | off | Preview what would be removed — no files are written |
| `--include-transitive` | off | Also list unused transitive packages (they cannot be removed from the project file) |
| `--ignore <pkg>` | _(none)_ | Suppress a package from analysis (repeatable) |

`fix` only touches the matching `<PackageReference>` lines and keeps the rest of the file as-is. It exits with `2` if any project could not be scanned or written.

**Examples**

```bash
# Preview what would be removed (safe — no changes written)
slimdep fix MyApp.csproj --dry-run

# Remove unused packages from a single project
slimdep fix MyApp.csproj

# Preview across an entire solution
slimdep fix C:\Dev\MySolution --dry-run

# Remove across an entire solution
slimdep fix C:\Dev\MySolution

# Include transitive packages
slimdep fix MyApp.csproj --include-transitive
```

After running `fix`, update the lockfile:

```bash
dotnet restore
```

**Sample output**

```
Found 2 project(s).

Scanning MyApp.Api.csproj ...
  Unused packages (2):
    - Newtonsoft.Json 13.0.3
    - Serilog.Sinks.File 5.0.0
  Removed 2 package(s) from MyApp.Api.csproj.

Scanning MyApp.Core.csproj ...
  No unused packages found.

Run 'dotnet restore' to update the lockfile.
```

---

## Help

```bash
slimdep --help
slimdep scan --help
slimdep fix --help
slimdep ignore --help
```

---

## CI Pipeline Integration

SlimDep exits with code `1` when unused packages are found, making it a drop-in CI gate.

**GitHub Actions:**
```yaml
- name: Check for unused NuGet packages
  run: slimdep scan .
```

**Azure DevOps:**
```yaml
- script: slimdep scan .
  displayName: 'SlimDep: Check unused packages'
```

For a JSON artifact in CI:
```bash
slimdep scan . --format json --output slimdep-report.json
```

---

## Known Limitations

- Matching is by namespace. A package that adds types to a namespace you already import (common with `Microsoft.Extensions.*` extension methods) counts as used.
- Packages used only through XAML `x:Type` or code-behind without a `using`/`clr-namespace` may be reported as unused. Use `--dry-run` to review before fixing.
- F# and VB.NET projects are not supported.
- Requires `obj/project.assets.json` — run `dotnet restore` first.
- Source generators and build-only packages (no exported namespaces) are automatically excluded from results.

---

## Tips

- **Run `dotnet restore` before scanning** — SlimDep reads `obj/project.assets.json` which is created by restore.
- **Use `--dry-run` before `fix`** — always preview before writing changes.
- **Add to CI** — `slimdep scan` exits with code `1` when unused packages are found, making it easy to fail a pipeline.
- **Recursive scan** — pass a folder path instead of a `.csproj` to scan all projects at once.
