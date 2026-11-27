import * as vscode from 'vscode';
import * as path from 'path';

export interface ScanResultJson {
    projectPath: string;
    totalDirectPackages: number;
    unusedCount: number;
    durationMs: number;
    unusedPackages: Array<{ name: string; version?: string; reason: string; transitive?: boolean }>;
}

export class PackageItem extends vscode.TreeItem {
    constructor(
        public readonly packageName: string,
        public readonly version: string | undefined,
        public readonly projectPath: string,
        public readonly reason: string,
        /** Transitive findings are informational: `slimdep fix` cannot remove them. */
        public readonly transitive: boolean = false,
    ) {
        super(packageName, vscode.TreeItemCollapsibleState.None);
        this.description = transitive ? `${version ?? ''} (transitive)`.trim() : (version ?? '');
        this.tooltip = transitive ? `${reason}\nTransitive: not removable from the project file.` : reason;
        this.iconPath = transitive
            ? new vscode.ThemeIcon('info', new vscode.ThemeColor('charts.blue'))
            : new vscode.ThemeIcon('package', new vscode.ThemeColor('charts.orange'));
        // Same context value so "Ignore" still works for both kinds
        this.contextValue = 'unusedPackage';
    }
}

export class ProjectItem extends vscode.TreeItem {
    public readonly packages: PackageItem[];

    constructor(projectPath: string, packages: PackageItem[]) {
        const label = path.basename(projectPath);
        super(label, vscode.TreeItemCollapsibleState.Expanded);
        this.packages = packages;
        this.tooltip = projectPath;
        const removable = packages.filter(p => !p.transitive).length;
        const transitive = packages.length - removable;
        this.description = transitive > 0 ? `${removable} unused, ${transitive} transitive` : `${removable} unused`;
        this.iconPath = new vscode.ThemeIcon('project', new vscode.ThemeColor('charts.red'));
        this.contextValue = 'project';
    }
}

export class UnusedPackagesProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<vscode.TreeItem | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private _projects: ProjectItem[] = [];

    addResult(projectPath: string, data: ScanResultJson): void {
        if (data.unusedCount === 0) return;

        const packageItems = data.unusedPackages.map(p =>
            new PackageItem(p.name, p.version, projectPath, p.reason, p.transitive === true)
        );

        this._projects.push(new ProjectItem(projectPath, packageItems));
    }

    clear(): void {
        this._projects = [];
    }

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getAllItems(): PackageItem[] {
        return this._projects.flatMap(p => p.packages);
    }

    /** Findings `slimdep fix` can actually remove (direct references only). */
    getRemovableItems(): PackageItem[] {
        return this.getAllItems().filter(p => !p.transitive);
    }

    getProjectPaths(): string[] {
        return this._projects.map(p => p.tooltip as string);
    }

    /**
     * After a successful `slimdep fix`, drop the removed (direct) findings for those projects.
     * Re-scanning straight away would bring them back: `fix` edits the .csproj but
     * project.assets.json keeps listing them until the next `dotnet restore`.
     * Transitive findings stay — fix never removes them.
     */
    markFixed(projectPaths: string[]): void {
        const fixedSet = new Set(projectPaths.map(p => p.toLowerCase()));
        this._projects = this._projects
            .map(project => {
                if (!fixedSet.has((project.tooltip as string).toLowerCase())) return project;
                const remaining = project.packages.filter(p => p.transitive);
                return remaining.length === project.packages.length
                    ? project
                    : new ProjectItem(project.tooltip as string, remaining);
            })
            .filter(project => project.packages.length > 0);
    }

    /** Projects with at least one removable finding. */
    getRemovableProjectPaths(): string[] {
        return this._projects
            .filter(p => p.packages.some(pkg => !pkg.transitive))
            .map(p => p.tooltip as string);
    }

    getProjectCount(): number {
        return this._projects.length;
    }

    getResultForProject(projectPath: string): ProjectItem | undefined {
        return this._projects.find(p => (p.tooltip as string) === projectPath);
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: vscode.TreeItem): vscode.TreeItem[] {
        if (!element) return this._projects;
        if (element instanceof ProjectItem) return element.packages;
        return [];
    }
}
