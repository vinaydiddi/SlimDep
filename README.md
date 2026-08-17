# SlimDep

[![NuGet](https://img.shields.io/nuget/v/SlimDep.svg)](https://nuget.org/packages/SlimDep)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SlimDep.svg)](https://nuget.org/packages/SlimDep)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**SlimDep** is a .NET global tool (`v1.1.0`) that detects and removes unused NuGet package references from `.csproj` files.

Over time, .NET projects accumulate dead package references — packages added during development or experimentation that are never cleaned up. SlimDep finds them automatically and can remove them in one command.

> Supports **.NET 8, 9, and 10**.

---

## Installation

```bash
dotnet tool install --global SlimDep
```

Once installed, the `slimdep` command is available everywhere on your machine.

```bash
dotnet tool update --global SlimDep    # update
dotnet tool uninstall --global SlimDep # uninstall
```

---

## Before You Scan

Your project must be restored so `obj/project.assets.json` exists:

```bash
dotnet restore
```

> SlimDep does not require a build — only a restore.

---

## Commands

### `slimdep --version`

```bash
slimdep --version
# output: 1.1.0
```

---

### `slimdep scan` — Detect unused packages

```bash
slimdep scan <project> [options]
```

**Argument**

| Argument | Description |
|---|---|
| `<project>` | Path to a `.csproj` file **or** a folder (all `.csproj` files found recursively, `obj/` excluded) |

**Options**

| Option | Default | Description |
|---|---|---|
| `--format` | `console` | Output format: `console`, `json`, or `sarif` |
| `--output <file>` | _(stdout)_ | Write report to a file instead of printing |
| `--include-transitive` | off | Also check transitive (indirect) packages |
| `--ignore <pkg>` | _(none)_ | Suppress a package from analysis (repeatable) |
| `-?, -h, --help` | | Show help |

**Examples:**
```bash
# Scan a single project
slimdep scan MyApp.csproj

# Scan an entire solution folder (all .csproj files found recursively)
slimdep scan C:\Dev\MySolution

# JSON output saved to a file
slimdep scan MyApp.csproj --format json --output report.json

# SARIF output — consumed by GitHub Code Scanning and Azure DevOps
slimdep scan MyApp.csproj --format sarif --output results.sarif

# Include transitive dependencies
slimdep scan MyApp.csproj --include-transitive
```

**Sample console output:**
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

  ✓ All packages are used. Nothing to remove.
```

**Sample JSON output** (`--format json`):
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

**Sample SARIF output** (`--format sarif`) — paste into GitHub Code Scanning or Azure DevOps for inline PR annotations:
```json
{
  "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
  "version": "2.1.0",
  "runs": [
    {
      "tool": { "driver": { "name": "SlimDep", "version": "1.1.0", "rules": [{ "id": "SLIMDEP001" }] } },
      "results": [
        {
          "ruleId": "SLIMDEP001",
          "level": "warning",
          "message": { "text": "Unused package reference: Newtonsoft.Json 13.0.3. No namespace from this package appears in any using directive." },
          "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "file:///C:/Dev/MySolution/src/MyApp.Api/MyApp.Api.csproj" }, "region": { "startLine": 12 } } }]
        }
      ]
    }
  ]
}
```

**Exit codes:** `0` = all clean · `1` = unused packages found · `2` = error

---

### `slimdep fix` — Remove unused packages

```bash
slimdep fix <project> [options]
```

**Argument**

| Argument | Description |
|---|---|
| `<project>` | Path to a `.csproj` file **or** a folder (all `.csproj` files found recursively, `obj/` excluded) |

**Options**

| Option | Default | Description |
|---|---|---|
| `--dry-run` | off | Preview removals — no files written |
| `--include-transitive` | off | Also remove unused transitive packages |
| `--ignore <pkg>` | _(none)_ | Suppress a package from analysis (repeatable) |
| `-?, -h, --help` | | Show help |

**Examples:**
```bash
# Always preview first
slimdep fix C:\Dev\MySolution --dry-run

# Remove unused packages from all projects in a folder
slimdep fix C:\Dev\MySolution

# Single project
slimdep fix MyApp.csproj

# Include transitive packages
slimdep fix MyApp.csproj --include-transitive
```

**Sample output:**
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

After fixing, run:
```bash
dotnet restore
```

---

### Help

```bash
slimdep --help
slimdep scan --help
slimdep fix --help
slimdep ignore --help
```

---

### `slimdep ignore` — Manage suppression list

The `.slimdepignore` file suppresses specific packages from analysis. SlimDep auto-detects it by walking up from the project directory.

```bash
slimdep ignore add <package> [--path <dir>]     # add to .slimdepignore
slimdep ignore remove <package> [--path <dir>]  # remove from .slimdepignore
slimdep ignore list [--path <dir>]              # show current entries
```

**Examples:**
```bash
# Suppress a package used via reflection or DI
slimdep ignore add Microsoft.EntityFrameworkCore.Design

# Suppress inline without creating a file (one-off)
slimdep scan . --ignore Newtonsoft.Json --ignore SomeAnalyzer

# View the current ignore list
slimdep ignore list

# Remove a suppression
slimdep ignore remove Microsoft.EntityFrameworkCore.Design
```

**Sample `.slimdepignore` file:**
```
# Packages used via reflection or DI registration
Microsoft.EntityFrameworkCore.Design
coverlet.collector
Swashbuckle.AspNetCore
```

> The `--path` option lets you target a specific directory. Without it, the current working directory is used.

---

## CI Pipeline Integration

`scan` exits with code `1` when unused packages are found — drop it straight into any pipeline:

**GitHub Actions — fail on unused packages:**
```yaml
- name: Check for unused NuGet packages
  run: slimdep scan .
```

**GitHub Actions — SARIF upload for inline PR annotations:**
```yaml
- name: SlimDep scan
  run: slimdep scan . --format sarif --output slimdep.sarif

- name: Upload SARIF results
  uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: slimdep.sarif
```

**Azure DevOps:**
```yaml
- script: slimdep scan .
  displayName: 'SlimDep: Check unused packages'
```

**Azure DevOps — SARIF artifact:**
```yaml
- script: slimdep scan . --format sarif --output $(Build.ArtifactStagingDirectory)/slimdep.sarif
  displayName: 'SlimDep: Scan for unused packages'
- task: PublishBuildArtifacts@1
  inputs:
    PathtoPublish: '$(Build.ArtifactStagingDirectory)/slimdep.sarif'
    ArtifactName: 'CodeAnalysisLogs'
```

---

## Supported Features

| Feature | Status |
|---|---|
| Scan a single `.csproj` file | ✅ |
| Scan a folder recursively (finds all `.csproj` files) | ✅ |
| Automatically skip `obj/` folders during recursive search | ✅ |
| Console output (human-readable with ✓/✗ indicators) | ✅ |
| JSON output (`--format json`) | ✅ |
| SARIF 2.1.0 output (`--format sarif`) with line-number locations | ✅ |
| GitHub Code Scanning / Azure DevOps inline PR annotations via SARIF | ✅ |
| Write report to a file (`--output`) | ✅ |
| Detect unused direct package references | ✅ |
| Detect unused transitive (indirect) package references (`--include-transitive`) | ✅ |
| Skip packages with no exported namespaces (build tools, source generators) | ✅ |
| Auto-remove unused packages from `.csproj` (`fix` command) | ✅ |
| Dry-run mode — preview removals without writing (`--dry-run`) | ✅ |
| CI-friendly exit codes (0 = clean, 1 = unused found, 2 = error) | ✅ |
| Multi-project fix in a single command | ✅ |
| Roslyn syntax-only analysis (no build required) | ✅ |
| Namespace-level matching via DLL reflection metadata | ✅ |
| In-process namespace cache (fast repeated scans) | ✅ |
| Fully-qualified name detection (Foo.Bar.Type without using directive) | ✅ |
| .slimdepignore file support (suppress specific packages) | ✅ |
| Inline `--ignore` flag (suppress without a file) | ✅ |
| `slimdep ignore` CLI for managing .slimdepignore (add / remove / list) | ✅ |
| Central Package Management (CPM) GlobalPackageReference detection | ✅ |
| XAML clr-namespace scanning (WPF, MAUI) | ✅ |
| Razor / CSHTML @using directive scanning (Blazor, MVC) | ✅ |
| Parallel multi-project scanning (bounded by CPU count) | ✅ |

---

## Prerequisites

- [.NET 8 SDK or later](https://dotnet.microsoft.com/download) (.NET 8, 9, or 10)
- Target project must be restored (`dotnet restore`) so that `obj/project.assets.json` exists

---

## Limitations

- Packages used only in XAML `x:Type` or code-behind without a `using` or `clr-namespace` declaration may be reported as unused
- Does not analyse `.cshtml` view imports from `_ViewImports.cshtml` automatically (add `@using` directives there to resolve)
- Requires `obj/project.assets.json` — run `dotnet restore` before scanning
- Source generators and build-only packages (no exported namespaces) are automatically excluded

---

## License

MIT © 2026 Vinay Diddi

