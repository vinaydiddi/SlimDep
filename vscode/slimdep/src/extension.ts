import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { UnusedPackagesProvider, PackageItem } from './providers/treeViewProvider';
import { CsprojCodeLensProvider } from './providers/codeLensProvider';
import { runSlimdep } from './slimDepRunner';

export function activate(context: vscode.ExtensionContext): void {
    const treeProvider = new UnusedPackagesProvider();

    context.subscriptions.push(
        vscode.window.registerTreeDataProvider('slimdep.unusedPackages', treeProvider),
        vscode.languages.registerCodeLensProvider(
            { language: 'xml', pattern: '**/*.csproj' },
            new CsprojCodeLensProvider(treeProvider)
        ),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('slimdep.scan', async () => {
            await scanWorkspace(treeProvider);
        }),

        vscode.commands.registerCommand('slimdep.refresh', async () => {
            await scanWorkspace(treeProvider);
        }),

        vscode.commands.registerCommand('slimdep.fix', async () => {
            await fixUnusedPackages(treeProvider);
        }),

        vscode.commands.registerCommand('slimdep.ignorePackage', async (item: PackageItem) => {
            await ignorePackage(item, treeProvider);
        }),
    );

    const config = vscode.workspace.getConfiguration('slimdep');
    if (config.get<boolean>('autoScanOnOpen', false)) {
        void scanWorkspace(treeProvider);
    }
}

async function scanWorkspace(provider: UnusedPackagesProvider): Promise<void> {
    const csprojFiles = await vscode.workspace.findFiles('**/*.csproj', '**/obj/**');

    if (csprojFiles.length === 0) {
        vscode.window.showInformationMessage('SlimDep: No .csproj files found in workspace.');
        return;
    }

    await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: 'SlimDep: Scanning...', cancellable: false },
        async () => {
            provider.clear();
            const config = vscode.workspace.getConfiguration('slimdep');
            const toolPath = config.get<string>('toolPath', 'slimdep');
            const includeTransitive = config.get<boolean>('includeTransitive', false);

            const failed: string[] = [];

            for (const file of csprojFiles) {
                const args = ['scan', file.fsPath, '--format', 'json'];
                if (includeTransitive) args.push('--include-transitive');

                const result = await runSlimdep(toolPath, args);
                if (result.success && result.data) {
                    provider.addResult(file.fsPath, result.data);
                } else if (result.notFound) {
                    vscode.window.showErrorMessage(
                        'SlimDep: CLI not found. Run: dotnet tool install --global SlimDep',
                        'Open Terminal'
                    ).then(action => {
                        if (action === 'Open Terminal') vscode.commands.executeCommand('workbench.action.terminal.new');
                    });
                    break;
                } else {
                    failed.push(`${path.basename(file.fsPath)}: ${result.error ?? 'unknown error'}`);
                }
            }

            if (failed.length > 0) {
                vscode.window.showWarningMessage(
                    `SlimDep: ${failed.length} project(s) could not be scanned (has 'dotnet restore' been run?). ${failed[0]}`
                );
            }

            provider.refresh();
        }
    );
}

async function fixUnusedPackages(provider: UnusedPackagesProvider): Promise<void> {
    // Transitive findings are informational — fix can't remove them, so don't offer to
    const items = provider.getRemovableItems();
    const projectPaths = provider.getRemovableProjectPaths();
    if (items.length === 0) {
        const transitiveOnly = provider.getAllItems().length > 0;
        vscode.window.showInformationMessage(transitiveOnly
            ? 'SlimDep: Only transitive packages were found. They cannot be removed from the project file.'
            : 'SlimDep: No unused packages to remove. Run a scan first.');
        return;
    }

    const confirm = await vscode.window.showWarningMessage(
        `SlimDep: Remove ${items.length} unused package(s) from ${projectPaths.length} project(s)?`,
        { modal: true },
        'Remove'
    );

    if (confirm !== 'Remove') return;

    const toolPath = vscode.workspace.getConfiguration('slimdep').get<string>('toolPath', 'slimdep');

    const failed: string[] = [];
    const succeeded: string[] = [];
    for (const projectPath of projectPaths) {
        const result = await runSlimdep(toolPath, ['fix', projectPath]);
        if (result.success) succeeded.push(projectPath);
        else failed.push(`${path.basename(projectPath)}: ${result.error ?? 'unknown error'}`);
    }

    if (failed.length > 0) {
        vscode.window.showErrorMessage(`SlimDep: Failed to update ${failed.length} project(s). ${failed.join('; ')}`);
    } else {
        vscode.window.showInformationMessage('SlimDep: Unused packages removed. Run dotnet restore before scanning again.');
    }

    // Don't re-scan here: until 'dotnet restore' runs, project.assets.json still lists the removed
    // references and they would reappear. Drop them locally instead; transitive findings and
    // projects that failed stay visible.
    provider.markFixed(succeeded);
    provider.refresh();
}

/**
 * Directory whose .slimdepignore SlimDep will read for this project: the nearest existing one
 * walking up from the project (SlimDep stops at the first it finds), else the project's own
 * workspace folder (correct in multi-root workspaces), else the project's directory.
 */
function ignoreFileDirectory(projectPath: string): string {
    const projectDir = path.dirname(projectPath);
    const workspaceDir = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(projectPath))?.uri.fsPath;

    let dir = projectDir;
    for (;;) {
        if (fs.existsSync(path.join(dir, '.slimdepignore'))) return dir;
        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }
    return workspaceDir ?? projectDir;
}

async function ignorePackage(item: PackageItem, provider: UnusedPackagesProvider): Promise<void> {
    const targetDir = ignoreFileDirectory(item.projectPath);
    const toolPath = vscode.workspace.getConfiguration('slimdep').get<string>('toolPath', 'slimdep');

    const result = await runSlimdep(toolPath, ['ignore', 'add', item.packageName, '--path', targetDir]);

    if (result.success) {
        vscode.window.showInformationMessage(
            `SlimDep: '${item.packageName}' added to ${path.join(targetDir, '.slimdepignore')}. Re-scanning...`);
        await scanWorkspace(provider);
    } else {
        vscode.window.showErrorMessage(`SlimDep: Failed to ignore package — ${result.error ?? 'unknown error'}`);
    }
}

export function deactivate(): void { /* nothing to clean up */ }
