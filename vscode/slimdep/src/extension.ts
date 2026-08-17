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

            for (const file of csprojFiles) {
                const args = ['scan', file.fsPath, '--format', 'json'];
                if (includeTransitive) args.push('--include-transitive');

                const result = await runSlimdep(toolPath, args);
                if (result.success && result.data) {
                    provider.addResult(file.fsPath, result.data);
                } else if (!result.success && result.error?.includes('not recognized')) {
                    vscode.window.showErrorMessage(
                        'SlimDep: CLI not found. Run: dotnet tool install --global SlimDep',
                        'Open Terminal'
                    ).then(action => {
                        if (action === 'Open Terminal') vscode.commands.executeCommand('workbench.action.terminal.new');
                    });
                    break;
                }
            }

            provider.refresh();
        }
    );
}

async function fixUnusedPackages(provider: UnusedPackagesProvider): Promise<void> {
    const items = provider.getAllItems();
    if (items.length === 0) {
        vscode.window.showInformationMessage('SlimDep: No unused packages to remove. Run a scan first.');
        return;
    }

    const confirm = await vscode.window.showWarningMessage(
        `SlimDep: Remove ${items.length} unused package(s) from ${provider.getProjectCount()} project(s)?`,
        { modal: true },
        'Remove'
    );

    if (confirm !== 'Remove') return;

    const toolPath = vscode.workspace.getConfiguration('slimdep').get<string>('toolPath', 'slimdep');

    for (const projectPath of provider.getProjectPaths()) {
        await runSlimdep(toolPath, ['fix', projectPath]);
    }

    vscode.window.showInformationMessage('SlimDep: Unused packages removed. Run dotnet restore to update lockfile.');
    provider.clear();
    provider.refresh();
}

async function ignorePackage(item: PackageItem, provider: UnusedPackagesProvider): Promise<void> {
    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (!workspaceFolders || workspaceFolders.length === 0) {
        vscode.window.showErrorMessage('SlimDep: No workspace folder found.');
        return;
    }

    const workspacePath = workspaceFolders[0].uri.fsPath;
    const toolPath = vscode.workspace.getConfiguration('slimdep').get<string>('toolPath', 'slimdep');

    const result = await runSlimdep(toolPath, ['ignore', 'add', item.packageName, '--path', workspacePath]);

    if (result.success) {
        vscode.window.showInformationMessage(`SlimDep: '${item.packageName}' added to .slimdepignore. Re-scanning...`);
        await scanWorkspace(provider);
    } else {
        vscode.window.showErrorMessage(`SlimDep: Failed to ignore package — ${result.error ?? 'unknown error'}`);
    }
}

export function deactivate(): void { /* nothing to clean up */ }
