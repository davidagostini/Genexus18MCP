import * as assert from "assert";
import * as vscode from "vscode";
import { GxInlineCompletionItemProvider } from "../../inlineCompletionProvider";
import { GxFileSystemProvider } from "../../gxFileSystem";

async function openDoc(content: string): Promise<vscode.TextDocument> {
  return vscode.workspace.openTextDocument({ content, language: "genexus" });
}

const NO_CONTEXT = {} as vscode.InlineCompletionContext;
const NOT_CANCELLED = {
  isCancellationRequested: false,
  onCancellationRequested: () => ({ dispose: () => {} }),
} as vscode.CancellationToken;

function labelsOf(
  result: vscode.InlineCompletionItem[] | vscode.InlineCompletionList,
): string[] {
  const items = Array.isArray(result) ? result : result.items;
  return items.map((i) =>
    typeof i.insertText === "string" ? i.insertText : i.insertText.value,
  );
}

suite("GxInlineCompletionItemProvider - context-aware ghost text", () => {
  test("suggests real structure fields and methods after '&var.' when the variable is an SDT", async () => {
    const doc = await openDoc("&cliente.");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).readObjectVariables = async () => [
      { name: "cliente", type: "SDTCliente", length: 0 },
    ];
    (fsProvider as any).getStructure = async () => ({
      children: [{ name: "Nombre", type: "Character" }],
    });

    const provider = new GxInlineCompletionItemProvider(fsProvider);
    const position = new vscode.Position(0, doc.getText().length);

    const result = await provider.provideInlineCompletionItems(
      doc,
      position,
      NO_CONTEXT,
      NOT_CANCELLED,
    );

    const labels = labelsOf(result);
    assert.ok(labels.includes("Nombre"), "expected the real SDT field as ghost text");
  });

  test("emits nothing for '&var.' when the variable is unknown (no guess)", async () => {
    const doc = await openDoc("&missing.");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).readObjectVariables = async () => [];

    const provider = new GxInlineCompletionItemProvider(fsProvider);
    const position = new vscode.Position(0, doc.getText().length);

    const result = await provider.provideInlineCompletionItems(
      doc,
      position,
      NO_CONTEXT,
      NOT_CANCELLED,
    );

    assert.strictEqual(labelsOf(result).length, 0);
  });

  test("AI path stays empty when genexus.inlineCompletion.ai is disabled (default)", async () => {
    const doc = await openDoc("&x = 1");
    const fsProvider = new GxFileSystemProvider();
    let called = false;
    (fsProvider as any).callMcpTool = async () => {
      called = true;
      return { completion: "should not be reached" };
    };

    const provider = new GxInlineCompletionItemProvider(fsProvider);
    const position = new vscode.Position(0, doc.getText().length);

    const result = await provider.provideInlineCompletionItems(
      doc,
      position,
      NO_CONTEXT,
      NOT_CANCELLED,
    );

    assert.strictEqual(labelsOf(result).length, 0);
    assert.strictEqual(called, false, "AI tool must not be called when the setting is off");
  });

  test("AI path degrades cleanly (no throw, empty ghost text) when genexus_ai_complete reports AiEndpointNotConfigured", async () => {
    const config = vscode.workspace.getConfiguration("genexus");
    await config.update(
      "inlineCompletion.ai",
      true,
      vscode.ConfigurationTarget.Global,
    );

    try {
      const doc = await openDoc("&x = 1");
      const fsProvider = new GxFileSystemProvider();
      (fsProvider as any).callMcpTool = async () => ({
        code: "AiEndpointNotConfigured",
      });

      const provider = new GxInlineCompletionItemProvider(fsProvider);
      const position = new vscode.Position(0, doc.getText().length);

      const result = await provider.provideInlineCompletionItems(
        doc,
        position,
        NO_CONTEXT,
        NOT_CANCELLED,
      );

      assert.strictEqual(labelsOf(result).length, 0);
    } finally {
      await config.update(
        "inlineCompletion.ai",
        undefined,
        vscode.ConfigurationTarget.Global,
      );
    }
  });

  test("AI path emits the completion as ghost text when configured and reachable", async () => {
    const config = vscode.workspace.getConfiguration("genexus");
    await config.update(
      "inlineCompletion.ai",
      true,
      vscode.ConfigurationTarget.Global,
    );

    try {
      const doc = await openDoc("&x = 1");
      const fsProvider = new GxFileSystemProvider();
      (fsProvider as any).callMcpTool = async () => ({
        completion: "&y = &x + 1",
      });

      const provider = new GxInlineCompletionItemProvider(fsProvider);
      const position = new vscode.Position(0, doc.getText().length);

      const result = await provider.provideInlineCompletionItems(
        doc,
        position,
        NO_CONTEXT,
        NOT_CANCELLED,
      );

      assert.ok(labelsOf(result).includes("&y = &x + 1"));
    } finally {
      await config.update(
        "inlineCompletion.ai",
        undefined,
        vscode.ConfigurationTarget.Global,
      );
    }
  });

  test("emits nothing when there is no active provider", async () => {
    const doc = await openDoc("&cliente.");
    const provider = new GxInlineCompletionItemProvider(undefined);
    const position = new vscode.Position(0, doc.getText().length);

    const result = await provider.provideInlineCompletionItems(
      doc,
      position,
      NO_CONTEXT,
      NOT_CANCELLED,
    );

    assert.strictEqual(labelsOf(result).length, 0);
  });
});
