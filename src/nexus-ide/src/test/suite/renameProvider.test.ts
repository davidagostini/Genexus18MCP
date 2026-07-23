import * as assert from "assert";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { GxRenameProvider } from "../../renameProvider";
import { GxFileSystemProvider } from "../../gxFileSystem";
import { GxShadowService } from "../../gxShadowService";

const NO_TOKEN = {} as vscode.CancellationToken;

// Attribute rename (the reachable path for the default word pattern, which excludes '&')
// warns the user with a Yes/No prompt about a reorg check. In a headless test run there is
// no one to click it, so we stub it out for the duration of each test to avoid hanging.
async function withStubbedReorgPrompt<T>(fn: () => Promise<T>): Promise<T> {
  const original = vscode.window.showWarningMessage;
  (vscode.window as any).showWarningMessage = async () => undefined;
  try {
    return await fn();
  } finally {
    (vscode.window as any).showWarningMessage = original;
  }
}

// The FileSystemProvider's Content-Cache constructor tries to reach a real gateway on
// construction of GxGatewayClient only lazily (on calls), so a bare `new GxFileSystemProvider()`
// is safe to use without a running backend as long as we stub `refactor`/`fireFileChange`.
suite("GxRenameProvider - editor refresh on rename", () => {
  test("re-hydrates and refreshes the open editor when the server refactor succeeds", async () => {
    // GxUriParser.isGeneXusUri only recognizes 'file' scheme docs that live under the
    // configured shadow root, so the mirror file must be created inside it.
    const shadowRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-rename-shadow-"));
    const shadowService = new GxShadowService("http://127.0.0.1:1", shadowRoot);
    const filePath = path.join(shadowRoot, "Total.gx");
    fs.writeFileSync(filePath, "&Total = 1");
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));
    assert.strictEqual(doc.isDirty, false, "precondition: freshly opened file must not be dirty");

    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).refactor = async () => ({ message: "Renamed OK" });

    let hydrateCalledWith: vscode.Uri | undefined;
    (shadowService as any).hydrateOpenedFile = async (uri: vscode.Uri) => {
      hydrateCalledWith = uri;
      return true;
    };

    let fireFileChangeCalledWith: vscode.Uri | undefined;
    (fsProvider as any).fireFileChange = (uri: vscode.Uri) => {
      fireFileChangeCalledWith = uri;
    };

    const provider = new GxRenameProvider(fsProvider, shadowService);
    const wordStart = doc.getText().indexOf("Total");
    const position = new vscode.Position(0, wordStart + 1);

    const edit = await withStubbedReorgPrompt(() =>
      provider.provideRenameEdits(doc, position, "NewTotal", NO_TOKEN),
    );

    assert.ok(edit, "expected a WorkspaceEdit to be returned on success");
    assert.ok(hydrateCalledWith, "expected hydrateOpenedFile to be invoked to refresh the editor");
    assert.strictEqual(hydrateCalledWith!.toString(), doc.uri.toString());
    assert.ok(fireFileChangeCalledWith, "expected fireFileChange to be invoked so VS Code reloads the content");
    assert.strictEqual(fireFileChangeCalledWith!.toString(), doc.uri.toString());
  });

  test("does NOT refresh the editor when the server refactor reports an error", async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-rename-doc-err-"));
    const filePath = path.join(tempDir, "Total.gx");
    fs.writeFileSync(filePath, "&Total = 1");
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));

    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).refactor = async () => ({ error: "Rename failed on server" });

    const shadowRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-rename-shadow-err-"));
    const shadowService = new GxShadowService("http://127.0.0.1:1", shadowRoot);

    let hydrateCalled = false;
    (shadowService as any).hydrateOpenedFile = async () => {
      hydrateCalled = true;
      return true;
    };

    let fireFileChangeCalled = false;
    (fsProvider as any).fireFileChange = () => {
      fireFileChangeCalled = true;
    };

    const provider = new GxRenameProvider(fsProvider, shadowService);
    const wordStart = doc.getText().indexOf("Total");
    const position = new vscode.Position(0, wordStart + 1);

    const edit = await withStubbedReorgPrompt(() =>
      provider.provideRenameEdits(doc, position, "NewTotal", NO_TOKEN),
    );

    assert.strictEqual(edit, undefined, "expected undefined to be returned on failure");
    assert.strictEqual(hydrateCalled, false, "hydrateOpenedFile must NOT be invoked on a failed rename");
    assert.strictEqual(fireFileChangeCalled, false, "fireFileChange must NOT be invoked on a failed rename");
  });

  test("skips refreshing a dirty document and warns the user instead of hydrating it", async () => {
    // Same shadow-root-backed 'file' document as the success test, but with an unsaved
    // in-editor edit applied so isDirty is true — this is exactly what the dirty-doc guard
    // (mirroring SyncManager.handleUpdateNotification) must skip.
    const shadowRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-rename-shadow-dirty-"));
    const shadowService = new GxShadowService("http://127.0.0.1:1", shadowRoot);
    const filePath = path.join(shadowRoot, "Total.gx");
    fs.writeFileSync(filePath, "&Total = 1");
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));

    const edit = new vscode.WorkspaceEdit();
    edit.insert(doc.uri, new vscode.Position(0, 0), "// dirty\n");
    await vscode.workspace.applyEdit(edit);
    assert.strictEqual(doc.isDirty, true, "precondition: the document must be dirty after an unsaved edit");

    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).refactor = async () => ({ message: "Renamed OK" });

    let hydrateCalled = false;
    (shadowService as any).hydrateOpenedFile = async () => {
      hydrateCalled = true;
      return true;
    };

    let fireFileChangeCalled = false;
    (fsProvider as any).fireFileChange = () => {
      fireFileChangeCalled = true;
    };

    let warnedAboutDirty = false;
    const originalShowWarning = vscode.window.showWarningMessage;
    (vscode.window as any).showWarningMessage = async (message: string) => {
      if (message.includes("unsaved changes")) {
        warnedAboutDirty = true;
      }
      return undefined;
    };

    try {
      const provider = new GxRenameProvider(fsProvider, shadowService);
      const wordStart = doc.getText().indexOf("Total");
      const position = new vscode.Position(0, wordStart + 1);

      const edit = await provider.provideRenameEdits(doc, position, "NewTotal", NO_TOKEN);

      assert.ok(edit, "expected a WorkspaceEdit to still be returned on success");
      assert.strictEqual(hydrateCalled, false, "hydrateOpenedFile must NOT be invoked for a dirty document");
      assert.strictEqual(fireFileChangeCalled, false, "fireFileChange must NOT be invoked for a dirty document");
      assert.ok(warnedAboutDirty, "expected the user to be warned that the dirty document was skipped");
    } finally {
      (vscode.window as any).showWarningMessage = originalShowWarning;
    }
  });
});
