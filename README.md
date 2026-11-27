# SlimDep

[![NuGet](https://img.shields.io/nuget/v/SlimDep.svg)](https://nuget.org/packages/SlimDep)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SlimDep.svg)](https://nuget.org/packages/SlimDep)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/vinaydiddi/SlimDep/blob/main/LICENSE)

A .NET global tool that finds and removes unused NuGet package references in `.csproj` files. It also reports transitive packages your code uses without referencing them. It never removes version pins or packages that work without code references.

- Needs a **restore**, not a build. Runs on **.NET 8, 9 and 10**.
- `fix` edits project files **byte-for-byte**: only the removed elements change.
- CI-ready: exit codes, JSON, and SARIF for GitHub Code Scanning and Azure DevOps.

**More:** [Usage guide & full output samples](https://github.com/vinaydiddi/SlimDep/blob/main/USAGE.md) · [Changelog](https://github.com/vinaydiddi/SlimDep/blob/main/CHANGELOG.md) · [VS Code extension](https://github.com/vinaydiddi/SlimDep/tree/main/vscode/slimdep)

## Install & quick start

```bash
dotnet tool install --global SlimDep     # update: dotnet tool update -g SlimDep · remove: dotnet tool uninstall -g SlimDep

dotnet restore                           # SlimDep reads the restore output (project.assets.json)
slimdep scan .                           # report
slimdep fix . --dry-run                  # preview removals
slimdep fix .                            # remove, then run 'dotnet restore' again
```

## Commands

`<project>` is a `.csproj` file or a folder. A folder is searched recursively for `.csproj` files; `obj/` and inaccessible folders are skipped.

| Command | Purpose |
|---|---|
| `slimdep scan <project>` | Report findings. Projects are scanned in parallel (up to one per CPU core). |
| `slimdep fix <project>` | Remove unused direct references (SLIMDEP001) from the project files. |
| `slimdep ignore add <pkg> [--path <dir>]` | Add a package to `<dir>/.slimdepignore` (default: current directory). Creates the file if needed. |
| `slimdep ignore remove <pkg> [--path <dir>]` | Remove a package from `<dir>/.slimdepignore`. |
| `slimdep ignore list [--path <dir>]` | Show the `.slimdepignore` SlimDep would use, searching upward from `<dir>`. |
| `slimdep --version` · `slimdep <command> --help` | Version and help. |

| Option | `scan` | `fix` | Description |
|---|:-:|:-:|---|
| `--format console\|json\|sarif` | ✓ | | Output format (default `console`). Other values are rejected. |
| `--output <file>` | ✓ | | Write the report to a file instead of stdout. |
| `--dry-run` | | ✓ | Show what would be removed; write nothing. |
| `--include-transitive` | ✓ | ✓ | Also list unused **transitive** packages (informational; never removed). |
| `--ignore <pkg>` | ✓ | ✓ | Suppress a package (repeatable). Combined with `.slimdepignore`. |
| `--no-default-ignores` | ✓ | ✓ | Turn off the [built-in safe list](#built-in-safe-list) and the `PrivateAssets="all"` exemption. |
| `--fail-on-undeclared` | ✓ | | Make SLIMDEP002 findings fail the scan (exit `1`). |

**Exit codes**

| Code | `scan` | `fix` |
|---|---|---|
| `0` | Nothing to remove. Informational findings (transitive, SLIMDEP002, pins, skips) don't fail the scan. | Success, including `--dry-run`. |
| `1` | Unused direct references found, or SLIMDEP002 findings with `--fail-on-undeclared`. | — |
| `2` | A project couldn't be analysed, e.g. not restored or an unreadable assets file. The other projects are still reported. | A project couldn't be analysed or written. |

Invalid command-line arguments exit with `1`, as reported by the argument parser. `ignore` exits `1` if `--path` doesn't exist, or if `remove` finds no `.slimdepignore`.

## What SlimDep reports

| Finding | Meaning | `fix` | Fails `scan`? |
|---|---|---|---|
| **SLIMDEP001** Unused reference | A direct `<PackageReference>` whose namespaces your code never uses. | Removed | Yes |
| Unused transitive (`--include-transitive`) | An indirect package your code doesn't use, shown with the reference that brings it in. Most transitive packages look like this. | Never touched | No |
| **SLIMDEP002** Undeclared dependency | A transitive package your code **does** use without referencing it. It can disappear when the package that brings it in changes, so add an explicit `<PackageReference>`. | Never touched | Only with `--fail-on-undeclared` |
| Version pin | An unused-looking direct reference that raises the version of a transitive dependency (usually a security fix). Removing it would downgrade that dependency. | Kept | No |
| Safe-list skip | An unused-looking package that works without code references (see below). | Kept | No |

**SLIMDEP002 avoids false positives.** It only counts names your code writes explicitly (`using`, fully-qualified names, `<Using>` items), and ignores:
- namespaces that a direct package also provides
- namespaces that **every** target framework provides: the shared framework on .NET Core / .NET 5+, the .NET Standard API surface on .NET Framework and .NET Standard (so `System.Text.Json` is ignored on `net8.0` but reported on `net48`)
- SDK implicit usings
- packages that arrive only through your own project references, or through a direct metapackage such as `xunit` → `xunit.assert`

**Version pins are checked per target framework.** A reference counts as a pin when every package that depends on it, in that framework's graph, asks for a lower version. It is kept if it pins in any framework, and the frameworks are named when it doesn't pin in all of them.

### Built-in safe list

| Category | Packages |
|---|---|
| Test platform & adapters | `Microsoft.NET.Test.Sdk`, `xunit.runner.*`, `NUnit3TestAdapter`, `NUnit.ConsoleRunner`, `NUnit.Analyzers`, `MSTest.TestAdapter`, `Microsoft.Testing.Platform.MSBuild`, `Microsoft.Testing.Extensions.*`, `JunitXml.TestLogger`, `XunitXml.TestLogger`, `NunitXml.TestLogger`, `GitHubActionsTestLogger` |
| Coverage | `coverlet.*`, `Microsoft.CodeCoverage` |
| Source link, versioning | `Microsoft.SourceLink.*`, `MinVer`, `Nerdbank.GitVersioning`, `GitVersion.MsBuild`, `Microsoft.Build.Tasks.Git` |
| Design-time & CLI tooling | `*.Design`, `Microsoft.EntityFrameworkCore.Tools`, `Microsoft.VisualStudio.Web.CodeGeneration.*`, `Microsoft.VisualStudio.Azure.Containers.Tools.Targets`, `Microsoft.NET.Build.Containers` |
| Front-end & runtime-loaded | `Microsoft.TypeScript.MSBuild`, `Microsoft.Web.LibraryManager.Build`, `Microsoft.AspNetCore.SpaProxy`, `SQLitePCLRaw.bundle_*`, `SQLitePCLRaw.provider.*` |
| Analyzers | `*.Analyzers`, `Microsoft.CodeAnalysis.NetAnalyzers`, `Microsoft.VisualStudio.Threading.Analyzers` |

References marked `PrivateAssets="all"` (development-only dependencies) are treated the same way. The safe list also applies to transitive packages. Skipped packages are always listed, so nothing disappears silently.

**Never reported at all:**
- packages with no assemblies (build tools, source generators, metapackages)
- packages the SDK adds automatically (`autoReferenced`, such as `NETStandard.Library`) and anything only they bring in
- Central Package Management `GlobalPackageReference` entries
- anything listed in `.slimdepignore` or passed with `--ignore`

## How usage is detected

| Source | What counts as using a package's namespace |
|---|---|
| Packages | `project.assets.json`, with all target frameworks merged. Public namespaces are read from each package's DLL metadata and cached in-process. The assets file is found via `MSBuildProjectExtensionsPath`, `BaseIntermediateOutputPath`, or `UseArtifactsOutput`/`ArtifactsPath`, falling back to `obj/` only if restore could still write there. A stale `obj/` left behind after redirecting is never used. |
| C# (Roslyn, syntax only) | `using`, `global using`, `using static`, aliases. Fully-qualified names in types and expressions, including `global::`. Every branch of `#if`/`#elif`/`#else`. |
| Project files | `<ImplicitUsings>` (per SDK: Web, Worker, BlazorWebAssembly, WinForms), `<Using>` items (including `Static`/`Alias`) in the project and `Directory.Build.props/.targets`, and generated `*.GlobalUsings.g.cs`. |
| XAML (`.xaml`, `.axaml`) | `xmlns` values `clr-namespace:` (WPF, MAUI, Avalonia) and `using:` (UWP, WinUI, Avalonia). |
| Razor (`.razor`, `.cshtml`) | `@using` (incl. static/alias), `@inject`, `@model`, `@inherits`, `@implements`, `@attribute`, `@typeparam`, `@layout`, `@code`/`@functions` blocks, inline `@Foo.Bar…` expressions, `@addTagHelper`. |

Which files are scanned: everything in the project folder except its own `bin/` and `obj/` and folders starting with `.`, plus linked `<Compile Include="…">` files and shared-project `.projitems` outside the folder. Folders containing another project are scanned too, because the SDK's default glob compiles them unless removed. Their usage keeps packages "used" but never triggers SLIMDEP002, and their `bin/`/`obj/` are skipped. MSBuild paths with `\` or `/` work on every OS.

`.slimdepignore`: SlimDep uses the nearest file found by searching upward from the project. It has one package name per line, matched case-insensitively; `#` starts a comment.

## Output formats

- **console**: a per-project summary with sections for unused, SLIMDEP002, version pins and safe-list skips.
- **json**: one object per project, or an array when zero or several projects are scanned. Fields:
  - `projectPath`, `totalDirectPackages`, `unusedCount`, `durationMs`
  - `unusedPackages[]`: `name`, `version`, `reason`, plus `transitive: true` for transitive packages
  - `undeclaredPackages[]`: `name`, `version`, `introducedBy`, `usedNamespaces`, `reason`
  - `pinnedPackages[]`: `name`, `version`, `transitiveVersion`, `requiredBy`, `targetFrameworks` (only when it pins in some frameworks but not all), `reason`
  - `skippedPackages[]`: `name`, `version`, `reason`

  The four arrays are always present.
- **sarif** (2.1.0): one run covering all projects, each project listed as an artifact; the tool version matches the package.
  - SLIMDEP001 results are `warning`s pointing at the `<PackageReference>` line.
  - Unused transitive packages and SLIMDEP002 results are `note`s. The `SLIMDEP002` rule is only included when it has results.
  - Pins and skips aren't included.

With JSON or SARIF and no `--output`, stdout contains only the report, so it can be piped directly. [Full samples →](https://github.com/vinaydiddi/SlimDep/blob/main/USAGE.md)

## How `fix` edits files

- It removes only SLIMDEP001 references. It prints a "Kept" line for each version pin and safe-list package, and lists transitive findings as not removable.
- It cuts only the matching `<PackageReference>` elements. Everything else stays byte-for-byte: quotes, attribute layout, entities, comments, whitespace, line endings, encoding (UTF-8, UTF-16 or a declared code page) and BOM.
- An `<ItemGroup>` left empty is removed, unless it has a `Condition` or `Label` or contains comments.
- A package declared outside the project file (e.g. `Directory.Build.props`) is listed as "not removed", for you to handle.
- Legacy project files that declare the MSBuild XML namespace are supported.
- Run `dotnet restore` afterwards to update the lock file.

## CI

```yaml
# GitHub Actions: fail on unused packages and annotate the PR
- run: dotnet tool install --global SlimDep && dotnet restore
- run: slimdep scan . --format sarif --output slimdep.sarif
- uses: github/codeql-action/upload-sarif@v3
  if: always()
  with: { sarif_file: slimdep.sarif }
```

On Azure DevOps, use `- script: slimdep scan .`. [More pipeline recipes →](https://github.com/vinaydiddi/SlimDep/blob/main/USAGE.md#ci-recipes)

## Limitations

- Matching is by namespace. A package whose types sit in a namespace you already import (common with `Microsoft.Extensions.*` extension methods) counts as used.
- Types used only via XAML `x:Type` or code-behind, with no `using`/`clr-namespace`, may be reported as unused. Review with `--dry-run`.
- MSBuild conditions aren't evaluated. SlimDep assumes the widest case:
  - conditional `<Using>` items count
  - conditional `<Using Remove>` is ignored
  - `ImplicitUsings` counts as on if any configuration enables it
- Paths built from properties other than the `$(MSBuild…)` built-ins (e.g. `$(SolutionDir)`) can't be resolved, whether in `<Compile Include>` or in the intermediate output path.
- `#if` code that needs some symbols defined and others undefined (e.g. `#if A && !B`) may be missed.
- On .NET Framework targets, the .NET Standard API stands in for what the framework provides.
- Razor files are scanned line by line, not with a full Razor parser.
- C# only: no F# or VB.NET. Input is a `.csproj` or a folder; `.sln`/`.slnx` files aren't accepted.
- `<Compile Remove>` isn't evaluated. Files the project excludes are still scanned, which can only make a package look used.

## Building from source

The tool runs on .NET 8, 9 or 10 (Windows, Linux, macOS). Building the repository needs a .NET SDK 9.0.200 or later because of `SlimDep.slnx`: `dotnet test SlimDep.slnx`. With an older SDK, use `dotnet test tests/SlimDep.UnitTests`. CI runs on Ubuntu, Windows and macOS.

## License

MIT © 2026 Vinay Diddi
