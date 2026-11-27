# Changelog

## 0.2.1 — Unreleased

Works best with SlimDep CLI 1.2.1 or later. It still reads the output of older CLI versions.

### Fixed
- Scan errors such as "project.assets.json not found" were mistaken for a missing CLI, which stopped the whole scan. A missing CLI is now detected only when the executable can't be launched.
- The tree view didn't work: it couldn't parse `slimdep scan --format json` output, because CLI 1.1.0 printed progress lines before the JSON.
- Transitive findings (`slimdep.includeTransitive`) are shown as informational (ⓘ). They are excluded from the removal prompt and still listed after **Remove All**. Before, the extension claimed it had removed them.
- **Remove All** re-scans afterwards instead of clearing the tree, and reports projects it couldn't update.
- CodeLens marks only the open project's removable packages. Before, it matched findings from every project.
- **Ignore** writes to the `.slimdepignore` SlimDep actually reads for that project: the nearest one searching upward, otherwise the project's workspace folder. Before, it always used the first workspace folder.
- Projects that fail to scan are summarised in a warning instead of being skipped silently.
- The `slimdep.autoScanOnOpen` description now matches what it does: it scans when the workspace opens.

## 0.2.0

- Tree view of unused packages per project, CodeLens on `.csproj` files, and the Scan / Remove All / Refresh / Ignore commands.
- Settings: `slimdep.toolPath`, `slimdep.autoScanOnOpen`, `slimdep.includeTransitive`.
