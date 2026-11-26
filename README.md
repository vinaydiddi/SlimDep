# SlimDep

[![NuGet](https://img.shields.io/nuget/v/SlimDep.svg)](https://nuget.org/packages/SlimDep)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SlimDep.svg)](https://nuget.org/packages/SlimDep)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**SlimDep** is a .NET global tool (`v1.2.0`) that detects and removes unused NuGet package references from `.csproj` files.

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
# output: 1.2.0
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
| `--include-transitive` | off | Also report transitive (indirect) packages whose namespaces are never used directly |
| `--ignore <pkg>` | _(none)_ | Suppress a package from analysis (repeatable) |
| `--no-default-ignores` | off | Also report packages on the [built-in safe list](#built-in-safe-list) and `PrivateAssets="all"` references |
| `--fail-on-undeclared` | off | Exit with `1` when [SLIMDEP002](#slimdep002--transitive-packages-used-directly) findings exist |
| `-?, -h, --help` | | Show help |

> **Transitive findings** are informational. They show which direct package or project reference brings each one in, but they can't be removed from your project file. `fix` lists them and leaves them alone. Most transitive packages are used only by the package that depends on them, so expect a long list.

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

  ⚠ 1 package(s) used directly but only referenced transitively (SLIMDEP002):

    • Microsoft.Data.Sqlite.Core 8.0.0
      Code uses Microsoft.Data.Sqlite but the package is only referenced transitively (via Microsoft.EntityFrameworkCore.Sqlite.Core). Add an explicit <PackageReference>.

  ℹ 1 package(s) kept as version pins:

    • SQLitePCLRaw.bundle_e_sqlite3 2.1.10
      Version pin: overrides 2.1.6 required by Microsoft.EntityFrameworkCore.Sqlite. Removing it would downgrade SQLitePCLRaw.bundle_e_sqlite3 to 2.1.6.

  ℹ 1 package(s) not reported (default safe list): coverlet.collector
    Use --no-default-ignores to analyse them.

Scanning MyApp.Core.csproj ...

  ✓ All packages are used. Nothing to remove.
```

**Sample JSON output** (`--format json`). A single project produces one object; a folder with several projects produces a JSON array of these objects. Transitive findings add `"transitive": true`. `undeclaredPackages`, `pinnedPackages` and `skippedPackages` are always present (empty when there is nothing to report). When JSON or SARIF goes to stdout, stdout holds only that document, so it can be piped straight into `jq` or another tool.
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
  ],
  "undeclaredPackages": [
    {
      "name": "Microsoft.Data.Sqlite.Core",
      "version": "8.0.0",
      "introducedBy": ["Microsoft.EntityFrameworkCore.Sqlite.Core"],
      "usedNamespaces": ["Microsoft.Data.Sqlite"],
      "reason": "Code uses Microsoft.Data.Sqlite but the package is only referenced transitively (via Microsoft.EntityFrameworkCore.Sqlite.Core). Add an explicit <PackageReference>."
    }
  ],
  "pinnedPackages": [
    {
      "name": "SQLitePCLRaw.bundle_e_sqlite3",
      "version": "2.1.10",
      "transitiveVersion": "2.1.6",
      "requiredBy": ["Microsoft.EntityFrameworkCore.Sqlite"],
      "reason": "Version pin: overrides 2.1.6 required by Microsoft.EntityFrameworkCore.Sqlite. Removing it would downgrade SQLitePCLRaw.bundle_e_sqlite3 to 2.1.6."
    }
  ],
  "skippedPackages": [
    { "name": "coverlet.collector", "version": "6.0.2", "reason": "On the built-in safe list (coverlet.*)" }
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
      "tool": { "driver": { "name": "SlimDep", "version": "1.2.0", "rules": [{ "id": "SLIMDEP001" }] } },
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

SLIMDEP002 findings appear as results with level `note`, and the `SLIMDEP002` rule is added to `rules` only when there are such findings. Version pins and safe-list skips are not SARIF results.

**Exit codes:** `0` = all clean · `1` = unused packages found (or SLIMDEP002 findings with `--fail-on-undeclared`) · `2` = error

---

## What SlimDep reports

### SLIMDEP001 — Unused package references

A direct `<PackageReference>` none of whose namespaces appear in your code. `fix` removes these.

### SLIMDEP002 — Transitive packages used directly

Your code uses a package that you never referenced. It only arrives because some other package depends on it. If that other package is upgraded or removed, it can quietly drop the dependency and break your build. Add an explicit `<PackageReference>` for it.

To keep false positives down, SlimDep reports a package only when your code explicitly imports or fully qualifies one of its namespaces. It ignores:
- namespaces that a direct package also provides
- namespaces that the .NET shared framework provides (e.g. `System.Text.Json`)
- SDK implicit usings
- packages that arrive only through your own project references
- packages brought in by a direct metapackage, such as `xunit` → `xunit.assert`, because the metapackage counts as the declaration

SLIMDEP002 findings are informational; `--fail-on-undeclared` makes them fail CI. `.slimdepignore` and `--ignore` suppress them too.

### Version pins

Teams often add a direct reference only to force a newer version of a transitive dependency, usually to pick up a security fix. If such a reference looks unused, removing it would quietly downgrade the package. Here is how SlimDep tells: other packages depend on it, but every one of them asks for a lower version. Such references are listed as **kept as version pins**, never reported as unused, and `fix` leaves them alone.

### Built-in safe list

Some packages do their job without your code ever importing them. SlimDep never reports them as unused:

| Category | Packages |
|---|---|
| Test platform & adapters | `Microsoft.NET.Test.Sdk`, `xunit.runner.*`, `NUnit3TestAdapter`, `MSTest.TestAdapter`, `Microsoft.Testing.Extensions.*`, test loggers |
| Coverage | `coverlet.*`, `Microsoft.CodeCoverage` |
| Source link & versioning | `Microsoft.SourceLink.*`, `MinVer`, `Nerdbank.GitVersioning`, `GitVersion.MsBuild` |
| Design-time & CLI tooling | `*.Design`, `Microsoft.EntityFrameworkCore.Tools`, `Microsoft.VisualStudio.Web.CodeGeneration.*`, container tooling |
| Runtime-loaded | `Microsoft.AspNetCore.SpaProxy`, `SQLitePCLRaw.bundle_*` |
| Analyzers | `*.Analyzers`, `Microsoft.CodeAnalysis.NetAnalyzers` |

References marked `PrivateAssets="all"` (development-only dependencies) are treated the same way. Skipped packages are listed so nothing disappears silently. Pass `--no-default-ignores` to analyse them anyway.

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
| `--include-transitive` | off | Also list unused transitive packages (they are reported, not removed) |
| `--no-default-ignores` | off | Also remove packages on the built-in safe list and `PrivateAssets="all"` references |
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

Version pins and safe-list packages are never removed; `fix` prints a "Kept" line for each so you can see why. `fix` edits only the matching `<PackageReference>` lines. Formatting, comments, line endings, BOM and the XML declaration stay as they were. An `<ItemGroup>` left empty is removed unless it has a `Condition`/`Label` or contains comments. A package declared outside the project file (e.g. in `Directory.Build.props`) is listed as "not removed" for you to handle by hand.

**Exit codes:** `0` = success · `2` = one or more projects could not be scanned or updated

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
| Report unused transitive (indirect) packages with the reference that brings them in (`--include-transitive`) | ✅ |
| Multi-targeted projects: all target frameworks analysed, `#if` blocks included | ✅ |
| SDK implicit usings, `<Using>` items and generated `GlobalUsings.g.cs` honoured | ✅ |
| Formatting-preserving `fix` (whitespace, comments, line endings, BOM kept) | ✅ |
| SLIMDEP002: transitive packages used directly without a `<PackageReference>` | ✅ |
| Version-pin detection (security pins are kept, never reported as unused) | ✅ |
| Built-in safe list for test, coverage, SourceLink and tooling packages (`--no-default-ignores` to disable) | ✅ |
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
- Matching is by namespace. If a package puts types into a namespace your code already imports for another reason (common with `Microsoft.Extensions.*` extension methods), it counts as used, and SDK implicit usings make this more likely
- MSBuild conditions on `<Using>` items are not evaluated; conditional usings are treated as always present
- Code under complex `#if` expressions that need some symbols defined and others undefined (e.g. `#if A && !B`) may be missed
- Requires `obj/project.assets.json` — run `dotnet restore` before scanning
- Source generators and build-only packages (no exported namespaces) are automatically excluded

---

## License

MIT © 2026 Vinay Diddi

