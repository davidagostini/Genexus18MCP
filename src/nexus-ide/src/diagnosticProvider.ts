import * as vscode from 'vscode';
import { GxFileSystemProvider } from './gxFileSystem';
import { GxUriParser } from './utils/GxUriParser';
import { Logger } from './utils/Logger';

export class GxDiagnosticProvider {
    private diagnosticCollection: vscode.DiagnosticCollection;
    private readonly pendingRefreshes = new Map<string, Promise<void>>();

    constructor(
        private readonly fsProvider: GxFileSystemProvider
    ) {
        this.diagnosticCollection = vscode.languages.createDiagnosticCollection('genexus');
    }

    public async refreshDiagnostics(document: vscode.TextDocument): Promise<void> {
        if (document.languageId !== 'genexus') return;
        if (this.fsProvider.isWorkspaceHydrating || this.fsProvider.hasInteractiveHydration) {
            return;
        }
        if (document.getText().startsWith("// GXMCP_PLACEHOLDER:")) {
            this.clear(document);
            return;
        }

        const docKey = document.uri.toString();
        const existing = this.pendingRefreshes.get(docKey);
        if (existing) {
            return existing;
        }

        const refresh = (async () => {
        try {
            const objName = this.getObjName(document);
            const result = await this.fsProvider.analyzeObject(
                objName,
                'linter',
                30000,
            );

            if (result && result.issues) {
                this.setDiagnostics(document, result.issues);
            } else {
                this.diagnosticCollection.delete(document.uri);
            }
        } catch (e) {
            Logger.error(`[Nexus IDE] Diagnostic error: ${e}`);
        } finally {
            this.pendingRefreshes.delete(docKey);
        }
        })();

        this.pendingRefreshes.set(docKey, refresh);
        return refresh;
    }

    public setDiagnostics(document: vscode.TextDocument, issues: any[]): void {
        const diagnostics: vscode.Diagnostic[] = [];
        const currentPart = this.getPartName(document.uri);

        for (const issue of issues) {
            // Re-apply the part filter logic
            if (currentPart === 'Variables') {
                if (issue.part !== 'Variables') continue;
            } else {
                if (issue.part === 'Variables') continue;
                if (issue.part !== currentPart && issue.part !== 'Logic') {
                    if (!(currentPart === 'Source' && issue.part === 'Procedure') && 
                        !(currentPart === 'Procedure' && issue.part === 'Source')) {
                        continue;
                    }
                }
            }

            const maxLine = Math.max(0, document.lineCount - 1);
            const line = Math.min(maxLine, Math.max(0, (issue.line || 1) - 1));
            let col = Math.max(0, (issue.column || 1) - 1);
            
            let range: vscode.Range;
            if (issue.snippet && issue.snippet.length > 0) {
                try {
                    const lineText = document.lineAt(line).text;
                    const snippetIndex = lineText.indexOf(issue.snippet);
                    if (snippetIndex >= 0) col = snippetIndex;
                } catch (e) {
                    Logger.debug(`[Nexus IDE] Diagnostic snippet-range lookup failed: ${e}`);
                }
                range = new vscode.Range(line, col, line, col + issue.snippet.length);
            } else {
                try {
                    const wordRange = document.getWordRangeAtPosition(new vscode.Position(line, col));
                    if (wordRange) {
                        range = wordRange;
                    } else {
                        range = new vscode.Range(line, col, line, col + 1);
                    }
                } catch {
                    range = new vscode.Range(line, col, line, col + 1);
                }
            }
            
            let severity = vscode.DiagnosticSeverity.Information;
            if (issue.severity === 'Critical' || issue.severity === 'Error') severity = vscode.DiagnosticSeverity.Error;
            else if (issue.severity === 'Warning') severity = vscode.DiagnosticSeverity.Warning;

            const diagnostic = new vscode.Diagnostic(range, issue.description, severity);
            diagnostic.code = issue.code;
            diagnostic.source = 'GeneXus LSP (Elite)';
            diagnostics.push(diagnostic);
        }
        this.diagnosticCollection.set(document.uri, diagnostics);
    }

    public async refreshAll(): Promise<void> {
        for (const editor of vscode.window.visibleTextEditors) {
            if (editor.document.languageId === 'genexus') {
                await this.refreshDiagnostics(editor.document);
            }
        }
    }

    public clear(document: vscode.TextDocument): void {
        this.diagnosticCollection.delete(document.uri);
    }

    private getObjName(document: vscode.TextDocument): string {
        return GxUriParser.getObjectName(document.uri);
    }

    private getPartName(uri: vscode.Uri): string {
        return this.fsProvider.getPart(uri);
    }

    public subscribeToEvents(context: vscode.ExtensionContext): void {
        context.subscriptions.push(vscode.workspace.onDidOpenTextDocument(doc => this.refreshDiagnostics(doc)));
        context.subscriptions.push(vscode.workspace.onDidSaveTextDocument(doc => this.refreshDiagnostics(doc)));
        
        // Debounced refresh for on-type diagnostics (Phase 1.1)
        let timeout: NodeJS.Timeout | undefined;
        context.subscriptions.push(vscode.workspace.onDidChangeTextDocument(e => {
            if (timeout) clearTimeout(timeout);
            timeout = setTimeout(() => this.refreshDiagnostics(e.document), 1500);
        }));

        context.subscriptions.push(vscode.workspace.onDidCloseTextDocument(doc => this.clear(doc)));
    }
}
