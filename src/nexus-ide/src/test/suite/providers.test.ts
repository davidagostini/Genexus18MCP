import * as assert from "assert";
import * as vscode from "vscode";
import { GxDefinitionProvider } from "../../definitionProvider";
import { GxDocumentSymbolProvider } from "../../symbolProvider";
import { GxCompletionItemProvider } from "../../completionProvider";
import { GxFileSystemProvider } from "../../gxFileSystem";

async function openDoc(content: string): Promise<vscode.TextDocument> {
  return vscode.workspace.openTextDocument({ content, language: "genexus" });
}

const NO_TOKEN = {} as vscode.CancellationToken;
const NO_CONTEXT = {} as vscode.CompletionContext;

suite("GxDefinitionProvider - local Sub resolution", () => {
  test("resolves 'do Subname' to the matching 'Sub Subname' declaration in the same file", async () => {
    const doc = await openDoc(["Sub 'MySub'", "    &x = 1", "Endsub", "", "do 'MySub'"].join("\n"));
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).queryObjects = async () => {
      throw new Error("remote lookup should not be reached for a local Sub");
    };

    const provider = new GxDefinitionProvider(fsProvider);
    const callLine = 4; // "do 'MySub'"
    const lineText = doc.lineAt(callLine).text;
    const position = new vscode.Position(callLine, lineText.indexOf("MySub") + 2);

    const definition = (await provider.provideDefinition(
      doc,
      position,
      NO_TOKEN,
    )) as vscode.Location;

    assert.ok(definition, "expected a Location to be returned");
    assert.strictEqual(definition.uri.toString(), doc.uri.toString());
    assert.strictEqual(definition.range.start.line, 0);
  });

  test("falls through to remote KB object search when the word is not a local Sub call", async () => {
    const doc = await openDoc("&result = MyAttribute");
    const fsProvider = new GxFileSystemProvider();
    let queried = "";
    (fsProvider as any).queryObjects = async (word: string) => {
      queried = word;
      return { results: [{ name: "MyAttribute", type: "Attribute" }] };
    };

    const provider = new GxDefinitionProvider(fsProvider);
    const wordStart = doc.getText().indexOf("MyAttribute");
    const position = new vscode.Position(0, wordStart + 1);

    const definition = (await provider.provideDefinition(
      doc,
      position,
      NO_TOKEN,
    )) as vscode.Location;

    assert.strictEqual(queried, "MyAttribute");
    assert.ok(definition, "expected a Location for the resolved KB object");
    // No shadow mirror is configured in this test, so GxUriParser.toEditorUri
    // falls back to its virtual gxkb18 scheme rather than a real file path.
    assert.strictEqual(definition.uri.toString(), "gxkb18:/Attribute/MyAttribute.gx");
  });

  test("returns undefined when there is no word at the given position", async () => {
    const doc = await openDoc("   ");
    const fsProvider = new GxFileSystemProvider();
    const provider = new GxDefinitionProvider(fsProvider);

    const definition = await provider.provideDefinition(doc, new vscode.Position(0, 1), NO_TOKEN);
    assert.strictEqual(definition, undefined);
  });
});

suite("GxDocumentSymbolProvider - regex extraction", () => {
  test("extracts Sub, Event and rule (parm/order/where) declarations", async () => {
    const source = [
      "Sub 'CalcularTotal'",
      "    &total = 0",
      "Endsub",
      "",
      "Event 'Enter'",
      "    &x = 1",
      "Endevent",
      "",
      "parm(in:&a, out:&b)",
      "order(AttributeId)",
      "where(AttributeId > 0)",
    ].join("\n");
    const doc = await openDoc(source);
    const provider = new GxDocumentSymbolProvider();

    const symbols = provider.provideDocumentSymbols(doc, NO_TOKEN);

    const byKind = (kind: vscode.SymbolKind) => symbols.filter((s) => s.kind === kind);
    assert.strictEqual(byKind(vscode.SymbolKind.Function).length, 1);
    assert.strictEqual(byKind(vscode.SymbolKind.Function)[0].name, "CalcularTotal");

    assert.strictEqual(byKind(vscode.SymbolKind.Event).length, 1);
    assert.strictEqual(byKind(vscode.SymbolKind.Event)[0].name, "Enter");

    assert.strictEqual(byKind(vscode.SymbolKind.Property).length, 3);
  });

  test("returns no symbols for a source file with none of the recognized declarations", async () => {
    const doc = await openDoc("&x = 1\n&y = &x + 1\n");
    const provider = new GxDocumentSymbolProvider();

    const symbols = provider.provideDocumentSymbols(doc, NO_TOKEN);
    assert.strictEqual(symbols.length, 0);
  });
});

suite("GxCompletionItemProvider - member-access parsing", () => {
  test("suggests structure fields and type methods after '&var.' when the variable is an SDT", async () => {
    const doc = await openDoc("&cliente.");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).readObjectVariables = async () => [
      { name: "cliente", type: "SDTCliente", length: 0 },
    ];
    (fsProvider as any).getStructure = async () => ({
      children: [{ name: "Nombre", type: "Character" }, { name: "Edad", type: "Numeric" }],
    });

    const provider = new GxCompletionItemProvider(fsProvider, () => "Source");
    const position = new vscode.Position(0, doc.getText().length);

    const items = (await provider.provideCompletionItems(
      doc,
      position,
      NO_TOKEN,
      NO_CONTEXT,
    )) as vscode.CompletionItem[];

    const labels = items.map((i) => (typeof i.label === "string" ? i.label : i.label.label));
    assert.ok(labels.includes("Nombre"));
    assert.ok(labels.includes("Edad"));
  });

  test("suggests Character methods after '&var.' when the variable is a plain Character", async () => {
    const doc = await openDoc("&nombre.");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).readObjectVariables = async () => [
      { name: "nombre", type: "Character", length: 50 },
    ];

    const provider = new GxCompletionItemProvider(fsProvider, () => "Source");
    const position = new vscode.Position(0, doc.getText().length);

    const items = (await provider.provideCompletionItems(
      doc,
      position,
      NO_TOKEN,
      NO_CONTEXT,
    )) as vscode.CompletionItem[];

    assert.ok(items.length > 0, "expected at least one Character method suggestion");
    assert.ok(items.every((i) => i.kind === vscode.CompletionItemKind.Method));
  });

  test("filters member suggestions by the partial text typed after the dot", async () => {
    const doc = await openDoc("&cliente.no");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).readObjectVariables = async () => [
      { name: "cliente", type: "SDTCliente", length: 0 },
    ];
    (fsProvider as any).getStructure = async () => ({
      children: [{ name: "Nombre", type: "Character" }, { name: "Edad", type: "Numeric" }],
    });

    const provider = new GxCompletionItemProvider(fsProvider, () => "Source");
    const position = new vscode.Position(0, doc.getText().length);

    const items = (await provider.provideCompletionItems(
      doc,
      position,
      NO_TOKEN,
      NO_CONTEXT,
    )) as vscode.CompletionItem[];

    const labels = items.map((i) => (typeof i.label === "string" ? i.label : i.label.label));
    assert.ok(labels.includes("Nombre"));
    assert.ok(!labels.includes("Edad"));
  });

  test("returns no member items when the variable referenced before the dot is unknown", async () => {
    const doc = await openDoc("&missing.");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).readObjectVariables = async () => [];

    const provider = new GxCompletionItemProvider(fsProvider, () => "Source");
    const position = new vscode.Position(0, doc.getText().length);

    const items = (await provider.provideCompletionItems(
      doc,
      position,
      NO_TOKEN,
      NO_CONTEXT,
    )) as vscode.CompletionItem[];

    assert.strictEqual(items.length, 0);
  });
});
