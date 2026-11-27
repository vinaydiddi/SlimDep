# SlimDep usage guide

Worked examples, full output samples, CI recipes and troubleshooting. Every option, rule and limitation is described in the [README](README.md). All samples below are real output from SlimDep 1.2.1, with paths shortened.

- [Typical workflow](#typical-workflow)
- [Scanning](#scanning): [console](#console-output) · [JSON](#json-output) · [SARIF](#sarif-output)
- [Fixing](#fixing)
- [Suppressing a package](#suppressing-a-package)
- [CI recipes](#ci-recipes)
- [Troubleshooting](#troubleshooting)

## Typical workflow

```bash
dotnet restore                      # 1. SlimDep reads project.assets.json; no build needed
slimdep scan .                      # 2. review the findings
slimdep fix . --dry-run             # 3. preview exactly what would be removed
slimdep fix .                       # 4. remove unused references
dotnet restore                      # 5. refresh the restore output / lock file
```

Act on the informational findings yourself:
- **SLIMDEP002:** add the suggested `<PackageReference>`.
- **Pins:** keep them.
- **Packages SlimDep can't see being used** (reflection, DI by convention): add them to `.slimdepignore`.

## Scanning

```bash
slimdep scan MyApp.csproj                                  # one project
slimdep scan C:\Dev\MySolution                             # every .csproj below a folder
slimdep scan . --include-transitive                        # also list unused transitive packages
slimdep scan . --format json --output slimdep.json         # machine-readable report
slimdep scan . --format sarif --output slimdep.sarif       # code-scanning report
slimdep scan . --ignore Swashbuckle.AspNetCore             # one-off suppression
slimdep scan . --no-default-ignores                        # also analyse safe-listed packages
slimdep scan . --fail-on-undeclared                        # SLIMDEP002 findings fail the run
```

### Console output

This project has one unused reference, one undeclared dependency and one version pin:

```
Scanning MyApp.Api.csproj ...

  Project : C:\Dev\MySolution\src\MyApp.Api\MyApp.Api.csproj
  Packages: 3 direct reference(s) analysed
  Duration: 554 ms

  ✗ 1 unused package(s) found:

    • Newtonsoft.Json 13.0.3
      No namespace from this package appears in any using directive

  Run 'slimdep fix <project>' to remove them automatically.

  ⚠ 1 package(s) used directly but only referenced transitively (SLIMDEP002):

    • Microsoft.Data.Sqlite.Core 8.0.0
      Code uses Microsoft.Data.Sqlite but the package is only referenced transitively (via Microsoft.EntityFrameworkCore.Sqlite.Core). Add an explicit <PackageReference>.

  ℹ 1 package(s) kept as version pins:

    • SQLitePCLRaw.core 2.1.10
      Version pin: overrides 2.1.6 required by Microsoft.Data.Sqlite.Core, SQLitePCLRaw.provider.e_sqlite3. Removing it would downgrade SQLitePCLRaw.core to 2.1.6.
```

When a safe-listed package looks unused, the report ends with:

```
  ℹ 1 package(s) not reported (default safe list): SQLitePCLRaw.bundle_e_sqlite3
    Use --no-default-ignores to analyse them.
```

A clean project prints `✓ All packages are used. Nothing to remove.` With `--include-transitive`, transitive packages appear in the unused list tagged `(transitive)`, with the reference that brings them in.

The pin entry for a multi-targeted project names the frameworks where the reference pins, e.g. `Version pin on net48: overrides 1.0.0 …`.

### JSON output

```json
{
  "projectPath": "C:\\Dev\\MySolution\\src\\MyApp.Api\\MyApp.Api.csproj",
  "totalDirectPackages": 3,
  "unusedCount": 1,
  "durationMs": 228.6,
  "unusedPackages": [
    {
      "name": "Newtonsoft.Json",
      "version": "13.0.3",
      "reason": "No namespace from this package appears in any using directive"
    }
  ],
  "undeclaredPackages": [
    {
      "name": "Microsoft.Data.Sqlite.Core",
      "version": "8.0.0",
      "introducedBy": ["Microsoft.EntityFrameworkCore.Sqlite.Core"],
      "usedNamespaces": ["Microsoft.Data.Sqlite"],
      "reason": "Code uses Microsoft.Data.Sqlite but the package is only referenced transitively (via Microsoft.EntityFrameworkCore.Sqlite.Core). Add an explicit \u003CPackageReference\u003E."
    }
  ],
  "pinnedPackages": [
    {
      "name": "SQLitePCLRaw.core",
      "version": "2.1.10",
      "transitiveVersion": "2.1.6",
      "requiredBy": ["Microsoft.Data.Sqlite.Core", "SQLitePCLRaw.provider.e_sqlite3"],
      "reason": "Version pin: overrides 2.1.6 required by Microsoft.Data.Sqlite.Core, SQLitePCLRaw.provider.e_sqlite3. Removing it would downgrade SQLitePCLRaw.core to 2.1.6."
    }
  ],
  "skippedPackages": []
}
```

More details:
- `<` and `>` are escaped as `\u003C`/`\u003E`; any JSON parser decodes them.
- Optional fields: transitive entries in `unusedPackages` add `"transitive": true`, and a pin that applies only to some target frameworks adds `"targetFrameworks"`.
- Scanning zero projects, or two or more, produces a JSON **array** of these objects.

```bash
# Names of all unused direct references, whether one project (object) or several (array) were scanned
slimdep scan . --format json | jq -r '(if type == "array" then .[] else . end) | .unusedPackages[] | select(.transitive != true) | .name'
```

```powershell
# PowerShell equivalent
(slimdep scan . --format json | ConvertFrom-Json) | ForEach-Object { $_.unusedPackages } | Where-Object { -not $_.transitive } | ForEach-Object name
```

### SARIF output

One run covers all scanned projects. The rule descriptions are shortened here:

```json
{
  "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
  "version": "2.1.0",
  "runs": [
    {
      "tool": {
        "driver": {
          "name": "SlimDep",
          "version": "1.2.1",
          "informationUri": "https://www.nuget.org/packages/SlimDep",
          "rules": [
            { "id": "SLIMDEP001", "name": "UnusedPackageReference", "defaultConfiguration": { "level": "warning" } },
            { "id": "SLIMDEP002", "name": "UndeclaredTransitivePackage", "defaultConfiguration": { "level": "note" } }
          ]
        }
      },
      "results": [
        {
          "ruleId": "SLIMDEP001",
          "level": "warning",
          "message": { "text": "Unused package reference: Newtonsoft.Json 13.0.3. No namespace from this package appears in any using directive." },
          "locations": [{ "physicalLocation": {
            "artifactLocation": { "uri": "file:///C:/Dev/MySolution/src/MyApp.Api/MyApp.Api.csproj" },
            "region": { "startLine": 17 } } }]
        },
        {
          "ruleId": "SLIMDEP002",
          "level": "note",
          "message": { "text": "Undeclared dependency: Microsoft.Data.Sqlite.Core 8.0.0. Code uses Microsoft.Data.Sqlite but the package is only referenced transitively (via Microsoft.EntityFrameworkCore.Sqlite.Core). Add an explicit <PackageReference>." },
          "locations": [{ "physicalLocation": {
            "artifactLocation": { "uri": "file:///C:/Dev/MySolution/src/MyApp.Api/MyApp.Api.csproj" } } }]
        }
      ],
      "artifacts": [
        { "location": { "uri": "file:///C:/Dev/MySolution/src/MyApp.Api/MyApp.Api.csproj" } }
      ]
    }
  ]
}
```

- **Rules:** `SLIMDEP002` appears under `rules` only when there is at least one SLIMDEP002 result.
- **Levels:** unused transitive packages (`--include-transitive`) are SLIMDEP001 results with level `note`.
- **Not included:** version pins and safe-list skips.

## Fixing

```bash
slimdep fix C:\Dev\MySolution --dry-run      # preview; writes nothing
slimdep fix C:\Dev\MySolution                # remove from every project below the folder
slimdep fix MyApp.csproj --ignore Foo.Bar    # keep Foo.Bar even if it looks unused
```

Dry run on the project above. The pin is kept and explained:

```
Scanning MyApp.Api.csproj ...
  Kept SQLitePCLRaw.core 2.1.10: version pin over 2.1.6 (Microsoft.Data.Sqlite.Core, SQLitePCLRaw.provider.e_sqlite3).
  Unused packages (1):
    - Newtonsoft.Json 13.0.3
  [dry-run] No changes written.
```

Without `--dry-run`:

```
  Removed 1 package(s) from MyApp.Api.csproj.

Run 'dotnet restore' to update the lockfile.
```

Other lines you may see:

| Line | Meaning |
|---|---|
| `Kept N package(s) on the default safe list: …` | Safe-listed packages that looked unused; left in place. |
| `Note: N transitive package(s) cannot be removed from the project file …` | `--include-transitive` findings; there is nothing to remove from your `.csproj`. |
| `Not removed — no <PackageReference> for these in X.csproj (likely declared in Directory.Build.props or an imported file):` | Remove these by hand where they are declared. |
| `Error: could not update X.csproj: …` | The file couldn't be read or written. `fix` exits `2`; other projects are still processed. |

Only the matching `<PackageReference>` elements are cut out of the file; everything else is left byte-for-byte. A real diff after `fix` on a project with single-quoted attributes and a multi-line element:

```diff
   <ItemGroup>
     <PackageReference Include='Azure.Core'   Version='1.44.1' />
-    <PackageReference
-        Include="Newtonsoft.Json"
-        Version="13.0.3" />
   </ItemGroup>
```

## Suppressing a package

SlimDep detects usage through namespaces. A package used only through reflection, assembly scanning or naming conventions can look unused. Suppress it:

```bash
slimdep ignore add Swashbuckle.AspNetCore                 # writes ./.slimdepignore (creates it if needed)
slimdep ignore add Swashbuckle.AspNetCore --path C:\Dev\MySolution
slimdep ignore list                                       # shows the file SlimDep would use, searching upward
slimdep ignore remove Swashbuckle.AspNetCore
slimdep scan . --ignore Swashbuckle.AspNetCore --ignore Foo.Bar   # one-off, no file
```

```
# .slimdepignore: one package per line, case-insensitive, '#' for comments
Swashbuckle.AspNetCore
Microsoft.ApplicationInsights.AspNetCore
```

Things to know:
- **Which file applies:** SlimDep reads the nearest `.slimdepignore` searching upward from each project. A file in the solution root covers every project below it unless a closer one exists.
- **Where `add` writes:** `add` and `remove` work on the file in `--path` (default: current directory) and don't search upward.
- **What it suppresses:** ignored packages are excluded from every finding, SLIMDEP002 included.

## CI recipes

Install and restore first (`dotnet tool install --global SlimDep`, `dotnet restore`). `scan` exits `1` on unused direct references and `2` on errors. Informational findings never fail the build unless you pass `--fail-on-undeclared`.

**GitHub Actions: fail the build**

```yaml
- name: Check for unused NuGet packages
  run: slimdep scan .
```

**GitHub Actions: inline PR annotations via Code Scanning**

```yaml
- name: SlimDep scan
  run: slimdep scan . --format sarif --output slimdep.sarif
- name: Upload SARIF
  if: always()                       # upload even when the scan step failed
  uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: slimdep.sarif
```

**GitHub Actions: also fail on undeclared dependencies**

```yaml
- run: slimdep scan . --fail-on-undeclared
```

**Azure DevOps: fail the build**

```yaml
- script: slimdep scan .
  displayName: 'SlimDep: Check unused packages'
```

**Azure DevOps: publish SARIF for the Scans tab**

```yaml
- script: slimdep scan . --format sarif --output $(Build.ArtifactStagingDirectory)/slimdep.sarif
  displayName: 'SlimDep: Scan for unused packages'
- task: PublishBuildArtifacts@1
  condition: always()
  inputs:
    PathtoPublish: '$(Build.ArtifactStagingDirectory)/slimdep.sarif'
    ArtifactName: 'CodeAnalysisLogs'
```

**Any CI: JSON report as an artifact**

```bash
slimdep scan . --format json --output slimdep-report.json
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `project.assets.json not found (looked in: …)` | The project isn't restored. Run `dotnet restore`. If restore writes elsewhere through a property SlimDep can't resolve (e.g. `$(SolutionDir)`), set `BaseIntermediateOutputPath`/`ArtifactsPath` using `$(MSBuildThisFileDirectory)` or a relative path. |
| A package you use is reported unused | It's used without its namespace appearing in code: reflection, DI by convention, XAML `x:Type`, or a path SlimDep can't resolve. Add it to `.slimdepignore`, and preview with `fix --dry-run`. |
| A package you don't need is *not* reported | Its namespace is imported for another reason (common with `Microsoft.Extensions.*` extension methods), or it's on the safe list. Try `--no-default-ignores`. |
| `--include-transitive` lists many packages | Expected: most transitive packages are used only by the package that depends on them. These are informational and never fail the scan. |
| SLIMDEP002 for a package you rely on deliberately | Add an explicit `<PackageReference>`, or suppress it with `.slimdepignore`. |
| `Error (X.csproj): …` and exit code `2` | That project couldn't be analysed. The message names the cause; the other projects' results are still reported. |
| Exit code `1` with no unused packages listed | Invalid command-line arguments (the parser exits `1`); check the error text above the output. |
| `fix` says "Not removed" | The reference is declared outside the project file. Edit `Directory.Build.props` or the imported file. |
