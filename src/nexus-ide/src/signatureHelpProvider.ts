import * as vscode from 'vscode';
import { nativeFunctions } from './gxNativeFunctions';
import { GxFileSystemProvider } from './gxFileSystem';
import { Logger } from './utils/Logger';

export class GxSignatureHelpProvider implements vscode.SignatureHelpProvider {
    constructor(private readonly provider: GxFileSystemProvider) {}

    async provideSignatureHelp(
        document: vscode.TextDocument,
        position: vscode.Position,
        _token: vscode.CancellationToken,
        _context: vscode.SignatureHelpContext
    ): Promise<vscode.SignatureHelp | undefined> {
        const lineText = document.lineAt(position).text;
        const lineUntilCursor = lineText.substring(0, position.character);

        // Match patterns like MyProc( or &var.Method( or even just (
        // We look for the last open parenthesis that isn't closed
        const lastParenIndex = lineUntilCursor.lastIndexOf('(');
        if (lastParenIndex === -1) return undefined;

        const prefix = lineUntilCursor.substring(0, lastParenIndex).trim();
        const match = prefix.match(/([a-zA-Z0-9_]+)$/);
        if (!match) return undefined;

        const name = match[1];
        const paramsText = lineUntilCursor.substring(lastParenIndex + 1);
        const paramIndex = (paramsText.match(/,/g) || []).length;

        // 1. Check Native Functions
        const native = nativeFunctions.find(f => f.name.toLowerCase() === name.toLowerCase());
        if (native) {
            const sig = new vscode.SignatureHelp();
            const si = new vscode.SignatureInformation(native.name + native.parameters, native.description);
            if (native.paramDetails) {
                si.parameters = native.paramDetails.map(p => new vscode.ParameterInformation(p.trim()));
            }
            sig.signatures = [si];
            sig.activeSignature = 0;
            sig.activeParameter = Math.min(paramIndex, si.parameters.length - 1);
            return sig;
        }

        // 2. KB Object Search
        try {
            const result = await this.provider.inspectObject(name, ['signature'], 15000);

            if (result && result.parameters) {
                const sig = new vscode.SignatureHelp();
                const label = result.signature || `${result.name}(${result.parameters.map((p: any) => p.accessor).join(', ')})`;
                const si = new vscode.SignatureInformation(label, `(GeneXus ${result.type})`);
                
                si.parameters = result.parameters.map((p: any) => {
                    const paramLabel = p.accessor;
                    const docParts = [];
                    if (p.direction) docParts.push(`**Direction**: ${p.direction}`);
                    if (p.typeName) docParts.push(`**Type**: ${p.typeName}${p.length ? '(' + p.length + ')' : ''}`);
                    
                    return new vscode.ParameterInformation(paramLabel, new vscode.MarkdownString(docParts.join('  \n')));
                });

                sig.signatures = [si];
                sig.activeSignature = 0;
                sig.activeParameter = Math.min(paramIndex, si.parameters.length - 1);
                return sig;
            }
        } catch (e) {
            Logger.error(`[Nexus IDE] Signature Help error: ${e}`);
        }

        return undefined;
    }
}
