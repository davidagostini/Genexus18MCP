import * as assert from "assert";
import * as vscode from "vscode";
import { GxCodeActionProvider } from "../../codeActionProvider";

async function openDoc(content: string): Promise<vscode.TextDocument> {
  return vscode.workspace.openTextDocument({ content, language: "genexus" });
}

const NO_TOKEN = {} as vscode.CancellationToken;

function contextWith(diagnostics: vscode.Diagnostic[]): vscode.CodeActionContext {
  return { diagnostics, only: undefined, triggerKind: vscode.CodeActionTriggerKind.Invoke };
}

function unusedVarDiagnostic(range: vscode.Range, code: string | undefined = "GX008"): vscode.Diagnostic {
  const diagnostic = new vscode.Diagnostic(range, "Variable '&Foo' is never used.", vscode.DiagnosticSeverity.Warning);
  diagnostic.code = code;
  diagnostic.source = "GeneXus LSP (Elite)";
  return diagnostic;
}

suite("GxCodeActionProvider - diagnostics-driven quick fixes", () => {
  test("offers 'Remove unused variable' when a GX008 diagnostic sits at the cursor", async () => {
    const doc = await openDoc("&Foo as Character\n&Bar as Character\n");
    const range = new vscode.Range(0, 0, 0, 4); // "&Foo"
    const diagnostic = unusedVarDiagnostic(range);

    const provider = new GxCodeActionProvider();
    const actions = await provider.provideCodeActions(doc, range, contextWith([diagnostic]), NO_TOKEN);

    assert.strictEqual(actions.length, 1);
    assert.ok(actions[0].title.includes("&Foo"));
    assert.strictEqual(actions[0].diagnostics?.[0], diagnostic);
    assert.strictEqual(actions[0].kind?.value, vscode.CodeActionKind.QuickFix.value);
    assert.ok(actions[0].edit, "expected a WorkspaceEdit that deletes the declaration line");
  });

  test("offers nothing when there is no diagnostic at the cursor, even on a &word", async () => {
    const doc = await openDoc("&Foo as Character\n");
    const range = new vscode.Range(0, 0, 0, 4); // "&Foo", no diagnostics passed in

    const provider = new GxCodeActionProvider();
    const actions = await provider.provideCodeActions(doc, range, contextWith([]), NO_TOKEN);

    assert.strictEqual(actions.length, 0);
  });

  test("offers nothing for a diagnostic whose code has no known fix", async () => {
    const doc = await openDoc("For Each\n    Commit\nEndFor\n");
    const range = new vscode.Range(1, 4, 1, 10); // "Commit"
    const diagnostic = new vscode.Diagnostic(range, "Avoid Commit inside For Each.", vscode.DiagnosticSeverity.Error);
    diagnostic.code = "GX001";

    const provider = new GxCodeActionProvider();
    const actions = await provider.provideCodeActions(doc, range, contextWith([diagnostic]), NO_TOKEN);

    assert.strictEqual(actions.length, 0);
  });

  test("falls back to message matching when code is not set", async () => {
    const doc = await openDoc("&Foo as Character\n");
    const range = new vscode.Range(0, 0, 0, 4);
    const diagnostic = new vscode.Diagnostic(range, "Variable '&Foo' is never used.", vscode.DiagnosticSeverity.Warning);
    // code intentionally left unset

    const provider = new GxCodeActionProvider();
    const actions = await provider.provideCodeActions(doc, range, contextWith([diagnostic]), NO_TOKEN);

    assert.strictEqual(actions.length, 1);
    assert.ok(actions[0].title.includes("&Foo"));
  });

  test("does not offer an action when the cursor is outside the diagnostic's range", async () => {
    const doc = await openDoc("&Foo as Character\n&Bar as Character\n");
    const diagRange = new vscode.Range(0, 0, 0, 4); // "&Foo" on line 0
    const cursorRange = new vscode.Range(1, 0, 1, 4); // "&Bar" on line 1
    const diagnostic = unusedVarDiagnostic(diagRange);

    const provider = new GxCodeActionProvider();
    const actions = await provider.provideCodeActions(doc, cursorRange, contextWith([diagnostic]), NO_TOKEN);

    assert.strictEqual(actions.length, 0);
  });
});
