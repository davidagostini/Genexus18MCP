import * as vscode from 'vscode';

/**
 * Diagnostic codes emitted by GxDiagnosticProvider (via genexus_analyze mode=linter,
 * see src/GxMcp.Worker/Services/LinterService.cs) that have a clearly safe, mechanical
 * fix wireable from this extension alone.
 *
 * Note: the linter has no "undeclared/unknown variable" rule (its codes are GX001-GX013,
 * GX020-GX022 - commit-in-loop, N+1, direct table access, blocking/dynamic call, missing
 * when-none/when-duplicate/parm, unused subroutine, gxButton event mismatch, missing gx-
 * prefix, out: parm disabled). So the previous "Create Variable" quick-fix on any `&word`
 * had no diagnostic to key off - it was pure blanket behavior, which this plan removes.
 * The one code with an obvious, safe, mechanical fix is GX008 (unused variable): delete
 * its declaration line. Every other code needs either a restructure the linter can't
 * safely automate, or a resolver call this provider doesn't have access to (it's
 * constructed with no service - see managers/ProviderManager.ts) - left as follow-up.
 */
const UNUSED_VARIABLE_CODE = 'GX008';
const UNUSED_VARIABLE_MESSAGE = /^Variable '(&\w+)' is never used\.$/;

export class GxCodeActionProvider implements vscode.CodeActionProvider {
    public static readonly kind = vscode.CodeActionKind.QuickFix;

    public async provideCodeActions(
        document: vscode.TextDocument,
        range: vscode.Range | vscode.Selection,
        context: vscode.CodeActionContext,
        _token: vscode.CancellationToken
    ): Promise<vscode.CodeAction[]> {
        const actions: vscode.CodeAction[] = [];

        for (const diagnostic of context.diagnostics) {
            if (!diagnostic.range.intersection(range) && !diagnostic.range.contains(range)) {
                continue;
            }

            const action = this.buildActionFor(document, diagnostic);
            if (action) actions.push(action);
        }

        return actions;
    }

    private buildActionFor(document: vscode.TextDocument, diagnostic: vscode.Diagnostic): vscode.CodeAction | undefined {
        // Key off the stable code; fall back to message match per the plan's STOP-condition
        // guidance in case a caller constructs a Diagnostic without setting `code`.
        const isUnusedVariable =
            String(diagnostic.code ?? '') === UNUSED_VARIABLE_CODE ||
            UNUSED_VARIABLE_MESSAGE.test(diagnostic.message);
        if (!isUnusedVariable) return undefined;

        const varName = this.extractVariableName(document, diagnostic);
        if (!varName) return undefined;

        const action = new vscode.CodeAction(`Remove unused variable ${varName}`, GxCodeActionProvider.kind);
        action.diagnostics = [diagnostic];
        action.isPreferred = true;

        const line = diagnostic.range.start.line;
        const edit = new vscode.WorkspaceEdit();
        edit.delete(document.uri, document.lineAt(line).rangeIncludingLineBreak);
        action.edit = edit;

        return action;
    }

    private extractVariableName(document: vscode.TextDocument, diagnostic: vscode.Diagnostic): string | undefined {
        const fromRange = document.getText(diagnostic.range);
        if (fromRange.startsWith('&')) return fromRange;

        const match = UNUSED_VARIABLE_MESSAGE.exec(diagnostic.message);
        return match ? match[1] : undefined;
    }
}
