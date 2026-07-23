import * as vscode from 'vscode';
import { GxUriParser } from './utils/GxUriParser';
import { GxFileSystemProvider } from './gxFileSystem';
import { GxShadowService } from './gxShadowService';
import { formatMcpErrorMessage } from './utils/McpErrorFormatter';

export class GxRenameProvider implements vscode.RenameProvider {
    constructor(
        private readonly provider: GxFileSystemProvider,
        private readonly shadowService?: GxShadowService,
    ) {}

    async provideRenameEdits(
        document: vscode.TextDocument,
        position: vscode.Position,
        newName: string,
        _token: vscode.CancellationToken
    ): Promise<vscode.WorkspaceEdit | undefined> {
        const range = document.getWordRangeAtPosition(position);
        if (!range) return undefined;

        const oldName = document.getText(range);
        const objName = this.getObjName(document);
        const isVariable = oldName.startsWith('&');
        
        try {
            await vscode.window.withProgress({
                location: vscode.ProgressLocation.Notification,
                title: `Renaming ${isVariable ? 'Variable' : 'Attribute'} ${oldName}...`,
                cancellable: false
            }, async () => {
                const result = await this.provider.refactor(
                    isVariable
                        ? {
                            action: 'RenameVariable',
                            objectName: objName,
                            target: oldName,
                            newName,
                        }
                        : {
                            action: 'RenameAttribute',
                            target: oldName,
                            newName,
                        },
                    300000,
                );

                if (result && result.error) {
                    throw new Error(result.error);
                }

                if (result && result.message) {
                    vscode.window.showInformationMessage(result.message);
                }
            });

            // Since the worker modifies the actual GeneXus object,
            // the safest way to show changes in the IDE is to tell the user that the object was saved.
            // VS Code expects a WorkspaceEdit to be returned to update the current editor live, but we
            // don't have a full multi-part editor sync yet. Instead, re-hydrate the affected open
            // editor(s) from the KB (same mechanism SyncManager uses) so the content is fresh, then
            // return an empty edit so VS Code doesn't attempt a naive local text replace.
            await this.refreshAffectedEditors(objName, isVariable);

            if (!isVariable) {
                const reorg = await vscode.window.showWarningMessage(
                    `Attribute renamed. Would you like to check for database impact (Run Reorg)?`, 
                    'Yes', 'No'
                );
                if (reorg === 'Yes') {
                    vscode.commands.executeCommand('nexus-ide.runReorg');
                }
            } else {
                vscode.window.showInformationMessage(`Variable renamed successfully. Please reload all parts to see changes.`);
            }
            
            // Trigger a refresh of diagnostics and references
            vscode.commands.executeCommand('nexus-ide.refreshDiagnostics');
            
            // return empty edit to avoid VS Code trying to do a local simple text replace which might be out of sync
            return new vscode.WorkspaceEdit(); 
        } catch (e: any) {
            vscode.window.showErrorMessage(formatMcpErrorMessage('Rename failed:', e));
            return undefined;
        }
    }

    private getObjName(document: vscode.TextDocument): string {
        return GxUriParser.getObjectName(document.uri);
    }

    /**
     * Re-hydrates the open editor(s) affected by a successful rename so the user sees the
     * change, instead of a silently stale editor. Variable renames only touch the current
     * object; attribute renames can touch any open object, so we refresh every open
     * GeneXus document. Mirrors SyncManager.handleUpdateNotification's dirty-doc guard.
     */
    private async refreshAffectedEditors(objectName: string, isVariable: boolean): Promise<void> {
        const docs = vscode.workspace.textDocuments.filter((doc) => {
            if (!GxUriParser.isGeneXusUri(doc.uri)) return false;
            if (!isVariable) return true;
            return GxUriParser.getObjectName(doc.uri).toLowerCase() === objectName.toLowerCase();
        });

        for (const doc of docs) {
            if (doc.isDirty) {
                vscode.window.showWarningMessage(
                    `Nexus IDE: skipped refreshing '${doc.uri.fsPath}' after rename — it has unsaved changes. Reload it manually to see the change.`,
                );
                continue;
            }

            if (doc.uri.scheme === 'file' && this.shadowService) {
                await this.shadowService.hydrateOpenedFile(doc.uri, this.provider);
            }

            this.provider.fireFileChange(doc.uri);
        }
    }
}
