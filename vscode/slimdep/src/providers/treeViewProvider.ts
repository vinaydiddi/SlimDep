import * as vscode from 'vscode';
import * as path from 'path';

export interface ScanResultJson {
    projectPath: string;
    totalDirectPackages: number;
    unusedCount: number;
    durationMs: number;
    unusedPackages: Array<{ name: string; version?: string; reason: string }>;
}

export class PackageItem extends vscode.TreeItem {
    constructor(
        public readonly packageName: string,
        public readonly version: string | undefined,
        public readonly projectPath: string,
        public readonly reason: string,
    ) {
        super(packageName, vscode.TreeItemCollapsibleState.None);
        this.description = version ?? '';
        this.tooltip = reason;
        this.iconPath = new vscode.ThemeIcon('package', new vscode.ThemeColor('charts.orange'));
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
        this.description = `${packages.length} unused`;
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
            new PackageItem(p.name, p.version, projectPath, p.reason)
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

    getProjectPaths(): string[] {
        return this._projects.map(p => p.tooltip as string);
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
