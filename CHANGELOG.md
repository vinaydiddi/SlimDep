# Changelog

All notable changes to the SlimDep CLI. The VS Code extension has its [own changelog](vscode/slimdep/CHANGELOG.md).

## 1.2.1 — Unreleased

Fixes from two code reviews of 1.2.0. **Upgrade recommended:** several of these could make `fix` remove a package the project still needs.

### Fixed: `fix` could remove needed packages
- Every branch of `#if`/`#elif`/`#else` chains is scanned. Before, only the first `#elif` branch was seen.
- `global::Foo.Bar.Method()` calls count as usage.
- Fully-qualified calls through lower-case root namespaces (`log4net.LogManager…`) count as usage.
- Projects located under a folder named `bin` or `obj` are scanned. Before, they scanned no source at all. Only the project's own `bin/` and `obj/` are skipped. Nested folders named `Bin`/`Obj` are scanned, and dot-folders are excluded, as in the SDK. Folders containing another project are scanned too, because the default glob compiles them. Their usage counts as "used" but not as SLIMDEP002 evidence, and their own `bin/`/`obj/` are skipped.
- Linked `<Compile Include="../Shared/*.cs"/>` files and shared-project `.projitems` outside the project folder are scanned.
- Version pins are evaluated per target framework. A reference that is redundant on `net8.0` but pins a higher version on `net48` is kept, and the reason names the framework.
- Conditional `<Using Remove>` and conditional `ImplicitUsings` overrides no longer hide usings that apply in other configurations.
- XAML `using:` namespaces (UWP, WinUI, Avalonia) and `.axaml` files are scanned. Matching is limited to `xmlns` attributes.
- Razor: `@inject`, `@model`, `@inherits`, `@implements`, `@attribute`, `@typeparam`, `@layout`, `@code`/`@functions` blocks, inline `@Foo.Bar…` expressions and `@addTagHelper` are scanned. `@using static X` and `@using A = X` are handled.
- `slimdep ignore add` no longer merges two entries when `.slimdepignore` has no trailing newline. The merge silently dropped both suppressions.

### Fixed: other
- `fix` cuts only the removed elements from the original text. Attribute quotes and layout, entities, encoding (UTF-16 and declared code pages) and BOM are preserved byte-for-byte. Before, UTF-16 project files failed to parse.
- `project.assets.json` is located via `MSBuildProjectExtensionsPath`, `BaseIntermediateOutputPath` and `UseArtifactsOutput`/`ArtifactsPath` before `obj/`, so artifacts-output projects work. When the output is unconditionally redirected, a stale `obj/` left behind is never analysed; SlimDep asks for a restore instead.
- Unexpected per-project errors exit `2` instead of `1` ("unused found"), and no longer discard the other projects' results. Inaccessible folders are skipped.
- With `--include-transitive`, transitive-only findings no longer make `scan` exit `1`.
- SLIMDEP002 judges framework namespaces per target framework. `System.Text.Json` used on `net48` through a transitive package is now reported.
- SLIMDEP002: `<Using Static="true">` and `Alias` items resolve as type names.
- SDK implicit packages (`autoReferenced`, e.g. `NETStandard.Library`) and anything only they bring in are never reported.
- The safe list also applies to unused transitive packages. `SQLitePCLRaw.provider.*` was added.
- The tool runs on machines with only .NET 9 or 10 installed (`RollForward=Major`).
- JSON: pins that apply to some target frameworks only include `targetFrameworks`.
- `fix`: when several `<PackageReference>`s on one line are removed, all of them are removed (one was skipped while still being reported as removed), and the line goes away entirely if nothing else is on it.
- Razor `@code` blocks: `#if` branches are scanned, and comments, verbatim/raw strings and char literals containing `}` no longer end the block early.
- No limit on `#if` symbols per file (32 before), so long `#elif` chains are scanned completely.
- SLIMDEP002: the framework exemption uses the target frameworks where the package actually appears.
- Cross-platform: MSBuild paths with `\` (`BaseIntermediateOutputPath`, `ArtifactsPath`, `.projitems` imports) resolve on Linux and macOS. File paths are compared case-sensitively on Linux.
- CI: build and tests run on Ubuntu, Windows and macOS.

## 1.2.0 — 2026-11-27 (tagged, not published to NuGet; superseded by 1.2.1)

### Added
- **SLIMDEP002:** reports transitive packages your code uses directly without a `<PackageReference>`.
  - Ignores framework-provided namespaces, namespaces shared with direct packages, SDK implicit usings, project references and metapackages (e.g. `xunit`).
  - Informational by default; `--fail-on-undeclared` makes it fail the scan.
  - Appears in console output, JSON (`undeclaredPackages`) and SARIF (rule `SLIMDEP002`, level `note`).
- **Version-pin detection:** a direct reference that raises a transitive dependency's version is kept and listed, so `fix` no longer downgrades security pins.
- **Built-in safe list:** these are never reported as unused:
  - test adapters, coverage collectors and analyzers
  - SourceLink, versioning and design-time tools
  - runtime-loaded providers
  - `PrivateAssets="all"` references

  Skipped packages are listed, and `--no-default-ignores` turns the list off.
- JSON output gains `undeclaredPackages`, `pinnedPackages` and `skippedPackages`.

## 1.1.1 — 2026-11-26 (not published separately; shipped with 1.2.x)

### Fixed
- `--include-transitive` did nothing. It now reports unused transitive packages with the reference that brings them in; `fix` lists them but never removes them.
- `--format json`/`sarif` to stdout emits only the report; progress lines had corrupted it. Multi-project JSON is a single array.
- Multi-targeted projects: every target framework is analysed, and usings inside `#if` blocks are counted.
- SDK implicit usings, `<Using>` items (project and `Directory.Build.*`) and generated `GlobalUsings.g.cs` are honoured. Before, `Microsoft.Extensions.Hosting` was removed from Worker projects.
- `fix` keeps formatting, comments, line endings, BOM and the XML declaration, and keeps conditional or commented `ItemGroup`s. It reports packages it couldn't find in the project file and exits `2` on errors.
- Legacy MSBuild-namespace project files and `global::`-qualified names are handled.
- The SARIF tool version comes from the assembly version.
- Invalid `--format` values are rejected instead of silently falling back to console.
- Implicit SDK references (`autoReferenced`) are never reported.

## 1.1.0 — 2026-08-17

### Added
- SARIF 2.1.0 output (`--format sarif`) for GitHub Code Scanning and Azure DevOps inline PR annotations.
- Each SARIF result points at the line of the unused `<PackageReference>`.
- Scanning a folder emits one SARIF document: one run, all findings, every project listed as an artifact.

## 1.0.1

### Fixed
- Supported-features table rendering on nuget.org (README line endings CRLF → LF).

## 1.0.0

### Added
- Fully-qualified name detection: `Foo.Bar.Type` without a `using` directive counts as usage.
- `.slimdepignore` file, `--ignore` flag, and `slimdep ignore add|remove|list`.
- Central Package Management: `GlobalPackageReference` entries are excluded.
- XAML `clr-namespace` scanning (WPF, MAUI) and Razor/CSHTML `@using` scanning (Blazor, MVC).
- Parallel multi-project scanning, bounded by CPU count.
