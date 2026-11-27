import * as vscode from 'vscode';
import { UnusedPackagesProvider } from './treeViewProvider';

/**
 * Provides CodeLens on PackageReference lines inside .csproj files.
 * Shows "⚠ Unused" above packages that were flagged by the last scan.
 */
export class CsprojCodeLensProvider implements vscode.CodeLensProvider {
    private readonly _onDidChangeCodeLenses = new vscode.EventEmitter<void>();
    readonly onDidChangeCodeLenses = this._onDidChangeCodeLenses.event;

    constructor(private readonly _provider: UnusedPackagesProvider) {
        _provider.onDidChangeTreeData(() => this._onDidChangeCodeLenses.fire());
    }

    provideCodeLenses(document: vscode.TextDocument): vscode.CodeLens[] {
        // Only direct (removable) findings for this project file; transitive findings have no
        // PackageReference line, and other projects' findings don't apply here.
        const docPath = document.uri.fsPath.toLowerCase();
        const unusedPackages = this._provider.getRemovableItems()
            .filter(p => p.projectPath.toLowerCase() === docPath);
        if (unusedPackages.length === 0) return [];

        const unusedNames = new Set(unusedPackages.map(p => p.packageName.toLowerCase()));
        const lenses: vscode.CodeLens[] = [];
        const text = document.getText();
        const lines = text.split('\n');

        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            const match = line.match(/PackageReference\s+Include="([^"]+)"/i);
            if (!match) continue;

            const packageName = match[1];
            if (!unusedNames.has(packageName.toLowerCase())) continue;

            const range = new vscode.Range(i, 0, i, line.length);

            lenses.push(new vscode.CodeLens(range, {
                title: '⚠ Unused — run slimdep fix to remove',
                command: 'slimdep.fix',
                tooltip: `${packageName} does not appear to be used in this project`,
            }));
        }

        return lenses;
    }
}
