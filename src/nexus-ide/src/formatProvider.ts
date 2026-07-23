import * as vscode from 'vscode';
import { GxFileSystemProvider } from './gxFileSystem';
import { Logger } from './utils/Logger';

export class GxFormatProvider implements vscode.DocumentFormattingEditProvider {
    constructor(private readonly provider: GxFileSystemProvider) {}

    private static readonly FORMAT_TIMEOUT_MS = 5000;

    async provideDocumentFormattingEdits(
        document: vscode.TextDocument,
        _options: vscode.FormattingOptions,
        _token: vscode.CancellationToken
    ): Promise<vscode.TextEdit[]> {
        try {
            const content = document.getText();
            const result = await this.provider.formatCode(
                content,
                GxFormatProvider.FORMAT_TIMEOUT_MS,
            );

            if (result && result.formatted) {
                const fullRange = new vscode.Range(
                    document.positionAt(0),
                    document.positionAt(content.length)
                );
                return [vscode.TextEdit.replace(fullRange, result.formatted)];
            }
        } catch (e) {
            Logger.error(`[Nexus IDE] Formatting error: ${e}`);
            vscode.window.setStatusBarMessage(
                "$(warning) GeneXus formatting skipped",
                3000,
            );
        }

        return [];
    }
}
