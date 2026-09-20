import * as vscode from 'vscode';
import { GxFileSystemProvider } from './gxFileSystem';
import { GxUriParser } from './utils/GxUriParser';

export class GxCodeLensProvider implements vscode.CodeLensProvider {
    private refCache = new Map<string, { count: number, time: number }>();

    constructor(private readonly provider: GxFileSystemProvider) {}

    async provideCodeLenses(
        document: vscode.TextDocument,
        _token: vscode.CancellationToken
    ): Promise<vscode.CodeLens[]> {
        const lenses: vscode.CodeLens[] = [];
        const objName = GxUriParser.getObjectName(document.uri);
        if (!objName) return lenses;

        // Add CodeLens at the first line of the document
        const range = new vscode.Range(0, 0, 0, 0);
        
        // We defer the actual count fetching to resolveCodeLens for performance
        const lens = new vscode.CodeLens(range);
        lenses.push(lens);

        return lenses;
    }

    async resolveCodeLens(
        codeLens: vscode.CodeLens,
        _token: vscode.CancellationToken
    ): Promise<vscode.CodeLens> {
        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor) return codeLens;

        const objName = GxUriParser.getObjectName(activeEditor.document.uri);
        if (!objName) return codeLens;

        // Use cache (5 minute ttl) to avoid hammering during scrolling/typing
        const cached = this.refCache.get(objName);
        if (cached && (Date.now() - cached.time < 300000)) {
            codeLens.command = {
                title: `${cached.count} references`,
                command: 'gx.showReferences',
                arguments: [objName]
            };
            return codeLens;
        }

        try {
            const results = await this.provider.queryObjects(`usedby:${objName}`, 1, 15000);

            const count = (results && results.count !== undefined) ? results.count : (results?.results?.length || 0);
            this.refCache.set(objName, { count, time: Date.now() });

            codeLens.command = {
                title: `${count} references`,
                command: 'gx.showReferences',
                arguments: [objName]
            };
        } catch {
            codeLens.command = {
                title: "0 references",
                command: ""
            };
        }

        return codeLens;
    }
}
