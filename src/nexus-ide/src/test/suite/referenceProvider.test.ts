import * as assert from "assert";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { GxReferenceProvider } from "../../referenceProvider";
import { GxFileSystemProvider } from "../../gxFileSystem";
import { GxUriParser } from "../../utils/GxUriParser";

const NO_TOKEN = {} as vscode.CancellationToken;
const NO_CONTEXT = {} as vscode.ReferenceContext;

let openDocCounter = 0;

// GxReferenceProvider requires GxUriParser.isGeneXusUri(document.uri) to be true, which
// only recognizes 'file' scheme docs living under the configured shadow root (or the
// virtual gxkb18 scheme). A plain openTextDocument({content}) is 'untitled' scheme and
// would be rejected before any provider logic runs, so tests open a real file under a
// configured shadow root instead.
async function openDoc(content: string): Promise<vscode.TextDocument> {
  const shadowRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-reference-doc-"));
  GxUriParser.configureShadowRoot(shadowRoot);
  const filePath = path.join(shadowRoot, `Doc${openDocCounter++}.gx`);
  fs.writeFileSync(filePath, content);
  return vscode.workspace.openTextDocument(vscode.Uri.file(filePath));
}

suite("GxReferenceProvider - real reference locations", () => {
  test("emits a real Range for each occurrence found in the deep-scanned objects' source", async () => {
    const doc = await openDoc("&x = AttributeBlue");
    const fsProvider = new GxFileSystemProvider();

    (fsProvider as any).queryObjects = async () => ({
      results: [
        { type: "Transaction", name: "Customer" },
        { type: "WebPanel", name: "CustomerList" },
      ],
    });

    const sources: Record<string, string> = {
      "Transaction:Customer": ["Att1", "AttributeBlue = 1", "&x = AttributeBlue"].join("\n"),
      "WebPanel:CustomerList": ["// no reference here", "&y = 2"].join("\n"),
    };
    (fsProvider as any).callMcpTool = async (_name: string, args: any) => ({
      source: sources[args.name] ?? "",
    });

    const provider = new GxReferenceProvider(fsProvider);
    const wordStart = doc.getText().indexOf("AttributeBlue");
    const position = new vscode.Position(0, wordStart + 1);

    const locations = await provider.provideReferences(doc, position, NO_CONTEXT, NO_TOKEN);

    const customerLocations = locations.filter((l) => l.uri.toString().includes("Customer.gx") && !l.uri.toString().includes("CustomerList"));
    assert.strictEqual(customerLocations.length, 2, "expected 2 real occurrences in the Customer transaction source");
    assert.strictEqual(customerLocations[0].range.start.line, 1);
    assert.strictEqual(customerLocations[1].range.start.line, 2);
    assert.ok(
      !customerLocations.some((l) => l.range.start.line === 0 && l.range.start.character === 0 && l.range.end.line === 0 && l.range.end.character === 0),
      "expected real ranges, not the old (0,0)-(0,0) placeholder",
    );

    const customerListLocations = locations.filter((l) => l.uri.toString().includes("CustomerList"));
    assert.strictEqual(
      customerListLocations.length,
      1,
      "object with no textual match falls back to a single object-level location",
    );
    assert.strictEqual(customerListLocations[0].range.start.line, 0);
    assert.strictEqual(customerListLocations[0].range.start.character, 0);
  });

  test("falls back to the object-level (0,0) location when source fetch fails for one object", async () => {
    const doc = await openDoc("&x = AttributeBlue");
    const fsProvider = new GxFileSystemProvider();

    (fsProvider as any).queryObjects = async () => ({
      results: [{ type: "Transaction", name: "Broken" }],
    });
    (fsProvider as any).callMcpTool = async () => {
      throw new Error("gateway unreachable");
    };

    const provider = new GxReferenceProvider(fsProvider);
    const wordStart = doc.getText().indexOf("AttributeBlue");
    const position = new vscode.Position(0, wordStart + 1);

    const locations = await provider.provideReferences(doc, position, NO_CONTEXT, NO_TOKEN);

    assert.strictEqual(locations.length, 1, "the object must not be lost when its source can't be fetched");
    assert.strictEqual(locations[0].range.start.line, 0);
    assert.strictEqual(locations[0].range.start.character, 0);
  });

  test("caps deep-scanning at the first 50 objects and still returns an object-level location for the rest", async () => {
    const doc = await openDoc("&x = AttributeBlue");
    const fsProvider = new GxFileSystemProvider();

    const totalObjects = 55;
    const results = Array.from({ length: totalObjects }, (_, i) => ({
      type: "Transaction",
      name: `Obj${i}`,
    }));
    (fsProvider as any).queryObjects = async () => ({ results });

    let scannedCount = 0;
    (fsProvider as any).callMcpTool = async () => {
      scannedCount++;
      return { source: "AttributeBlue" };
    };

    const provider = new GxReferenceProvider(fsProvider);
    const wordStart = doc.getText().indexOf("AttributeBlue");
    const position = new vscode.Position(0, wordStart + 1);

    const locations = await provider.provideReferences(doc, position, NO_CONTEXT, NO_TOKEN);

    assert.strictEqual(scannedCount, 50, "expected only the first 50 objects to be deep-scanned");
    assert.strictEqual(locations.length, totalObjects, "no object should be dropped: 50 real + 5 object-level fallback");
  });

  test("supports within-object variable references by scanning the open document instead of an unconditional skip", async () => {
    const doc = await openDoc(["&Total = 0", "&Total = &Total + 1", "&Other = 2"].join("\n"));
    const fsProvider = new GxFileSystemProvider();

    let queryObjectsCalled = false;
    (fsProvider as any).queryObjects = async () => {
      queryObjectsCalled = true;
      return { results: [] };
    };

    const provider = new GxReferenceProvider(fsProvider);
    const wordStart = doc.getText().indexOf("Total");
    const position = new vscode.Position(0, wordStart + 1);

    const locations = await provider.provideReferences(doc, position, NO_CONTEXT, NO_TOKEN);

    assert.strictEqual(queryObjectsCalled, false, "variable references must not go through the KB-wide usedby: query");
    assert.strictEqual(locations.length, 3, "expected all 3 occurrences of &Total in the open document");
    assert.ok(locations.every((l) => l.uri.toString() === doc.uri.toString()));
  });

  test("includeDeclaration: true returns all occurrences of a variable", async () => {
    const doc = await openDoc(["&Total = 0", "&Total = &Total + 1", "&Other = 2"].join("\n"));
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).queryObjects = async () => ({ results: [] });

    const provider = new GxReferenceProvider(fsProvider);
    const wordStart = doc.getText().indexOf("Total");
    const position = new vscode.Position(0, wordStart + 1);

    const locations = await provider.provideReferences(
      doc, position, { includeDeclaration: true } as vscode.ReferenceContext, NO_TOKEN,
    );

    assert.strictEqual(locations.length, 3);
  });

  test("includeDeclaration: false drops the first (declaration) occurrence of a variable", async () => {
    const doc = await openDoc(["&Total = 0", "&Total = &Total + 1", "&Other = 2"].join("\n"));
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).queryObjects = async () => ({ results: [] });

    const provider = new GxReferenceProvider(fsProvider);
    const wordStart = doc.getText().indexOf("Total");
    const position = new vscode.Position(0, wordStart + 1);

    const locations = await provider.provideReferences(
      doc, position, { includeDeclaration: false } as vscode.ReferenceContext, NO_TOKEN,
    );

    assert.strictEqual(locations.length, 2, "expected the first occurrence (the declaration) dropped");
  });

  test("includeDeclaration: false on a variable with zero occurrences returns [] (no negative slice)", async () => {
    const doc = await openDoc("&Unrelated = 1");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).queryObjects = async () => ({ results: [] });

    const provider = new GxReferenceProvider(fsProvider);
    const wordStart = doc.getText().indexOf("Unrelated");
    const position = new vscode.Position(0, wordStart + 1);

    const locationsInclude = await provider.provideReferences(
      doc, position, { includeDeclaration: true } as vscode.ReferenceContext, NO_TOKEN,
    );
    const locationsExclude = await provider.provideReferences(
      doc, position, { includeDeclaration: false } as vscode.ReferenceContext, NO_TOKEN,
    );

    assert.strictEqual(locationsInclude.length, 1);
    assert.strictEqual(locationsExclude.length, 0);
  });
});
