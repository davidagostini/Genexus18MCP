import * as vscode from 'vscode';

export class GxActionsProvider implements vscode.TreeDataProvider<ActionItem> {
    private _onDidChangeTreeData: vscode.EventEmitter<ActionItem | undefined | void> = new vscode.EventEmitter<ActionItem | undefined | void>();
    readonly onDidChangeTreeData: vscode.Event<ActionItem | undefined | void> = this._onDidChangeTreeData.event;

    getTreeItem(element: ActionItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: ActionItem): ActionItem[] {
        if (!element) {
            // Root categories
            return [
                new ActionItem('DevOps', vscode.TreeItemCollapsibleState.Expanded, 'devops', 'rocket'),
                new ActionItem('Modeling', vscode.TreeItemCollapsibleState.Expanded, 'modeling', 'layers'),
                new ActionItem('Quality & Testing', vscode.TreeItemCollapsibleState.Expanded, 'quality', 'check-all'),
                new ActionItem('Visualization', vscode.TreeItemCollapsibleState.Collapsed, 'viz', 'graph'),
                new ActionItem('AI Intelligence', vscode.TreeItemCollapsibleState.Expanded, 'ai', 'beaker')
            ];
        }

        if (element.contextValue === 'quality') {
            return [
                new ActionItem('Run Unit Tests (GXtest)', vscode.TreeItemCollapsibleState.None, 'action', 'beaker', 'nexus-ide.runTest'),
                new ActionItem('Run Performance Linter', vscode.TreeItemCollapsibleState.None, 'action', 'search-stop', 'nexus-ide.runLinter'),
                new ActionItem('Extract to Procedure...', vscode.TreeItemCollapsibleState.None, 'action', 'symbol-method', 'nexus-ide.extractProcedure')
            ];
        }

        if (element.contextValue === 'devops') {
            return [
                new ActionItem('Build KB', vscode.TreeItemCollapsibleState.None, 'action', 'play', 'nexus-ide.buildObject'),
                new ActionItem('Rebuild All', vscode.TreeItemCollapsibleState.None, 'action', 'zap', 'nexus-ide.rebuildAll'),
                new ActionItem('Re-Index KB (Search)', vscode.TreeItemCollapsibleState.None, 'action', 'search', 'nexus-ide.indexKb')
            ];
        }

        if (element.contextValue === 'modeling') {
            return [
                new ActionItem('New GeneXus Object...', vscode.TreeItemCollapsibleState.None, 'action', 'add', 'nexus-ide.newObject'),
                new ActionItem('Rename Attribute (Global)...', vscode.TreeItemCollapsibleState.None, 'action', 'edit', 'nexus-ide.renameAttribute'),
                new ActionItem('View Object History', vscode.TreeItemCollapsibleState.None, 'action', 'history', 'nexus-ide.viewHistory')
            ];
        }

        if (element.contextValue === 'viz') {
            return [
                new ActionItem('Generate Entity Diagram', vscode.TreeItemCollapsibleState.None, 'action', 'graph-left', 'nexus-ide.generateDiagram')
            ];
        }

        if (element.contextValue === 'ai') {
            return [
                new ActionItem('Auto-Fix Build Errors', vscode.TreeItemCollapsibleState.None, 'action', 'sparkle', 'nexus-ide.autoFix'),
                new ActionItem('Copy MCP Config for Copilot/Claude', vscode.TreeItemCollapsibleState.None, 'action', 'comment-discussion', 'nexus-ide.copyMcpConfig')
            ];
        }

        return [];
    }

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }
}

class ActionItem extends vscode.TreeItem {
    constructor(
        public readonly label: string,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState,
        public readonly contextValue: string,
        iconName: string,
        commandId?: string
    ) {
        super(label, collapsibleState);
        this.iconPath = new vscode.ThemeIcon(iconName);
        if (commandId) {
            this.command = {
                command: commandId,
                title: label
            };
        }
    }
}
