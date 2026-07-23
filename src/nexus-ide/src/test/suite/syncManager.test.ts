import * as assert from "assert";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { SyncManager } from "../../managers/SyncManager";
import { GxFileSystemProvider } from "../../gxFileSystem";
import { GxShadowService } from "../../gxShadowService";

// SyncManager.handleUpdateNotification is invoked from an SSE `data:` line whose
// `params.uri` is a `genexus://objects/<Name>` URI (see BroadcastResourceUpdated in
// Program.Notifications.cs). We drive the private handler directly instead of a real
// SSE connection, since `register()`/`startListening()` opens a real http.request against
// `provider.baseUrl` and would leave a live reconnect timer running in a headless test.
suite("SyncManager - dirty-doc-guarded refresh on resources/updated", () => {
  test("re-hydrates and refreshes a non-dirty open document that matches the updated object", async () => {
    const shadowRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-sync-clean-"));
    const shadowService = new GxShadowService("http://127.0.0.1:1", shadowRoot);
    const filePath = path.join(shadowRoot, "DebugGravar.gx");
    fs.writeFileSync(filePath, "&Total = 1");
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));
    assert.strictEqual(doc.isDirty, false, "precondition: freshly opened file must not be dirty");

    const fsProvider = new GxFileSystemProvider();

    let invalidatedWith: string | undefined;
    (shadowService as any).invalidateCache = (objectName: string) => {
      invalidatedWith = objectName;
    };

    let hydrateCalledWith: vscode.Uri | undefined;
    (shadowService as any).hydrateOpenedFile = async (uri: vscode.Uri) => {
      hydrateCalledWith = uri;
      return true;
    };

    let fireFileChangeCalledWith: vscode.Uri | undefined;
    (fsProvider as any).fireFileChange = (uri: vscode.Uri) => {
      fireFileChangeCalledWith = uri;
    };

    const syncManager = new SyncManager({ subscriptions: [] } as any, fsProvider, shadowService);
    await (syncManager as any).handleUpdateNotification("genexus://objects/DebugGravar");

    assert.strictEqual(invalidatedWith, "DebugGravar", "expected cache invalidation for the updated object");
    assert.ok(hydrateCalledWith, "expected hydrateOpenedFile to be invoked to refresh the editor");
    assert.strictEqual(hydrateCalledWith!.toString(), doc.uri.toString());
    assert.ok(fireFileChangeCalledWith, "expected fireFileChange to be invoked so VS Code reloads the content");
    assert.strictEqual(fireFileChangeCalledWith!.toString(), doc.uri.toString());
  });

  test("skips a dirty document instead of clobbering unsaved edits", async () => {
    // A distinct object name from the previous test: vscode.workspace.textDocuments
    // accumulates every doc opened so far in the test process, and the handler matches by
    // filename across ALL open docs (by design) — reusing a name would also match the other
    // test's already-open, non-dirty document and hydrate that one instead.
    const shadowRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-sync-dirty-"));
    const shadowService = new GxShadowService("http://127.0.0.1:1", shadowRoot);
    const filePath = path.join(shadowRoot, "DebugGravarDirty.gx");
    fs.writeFileSync(filePath, "&Total = 1");
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));

    const edit = new vscode.WorkspaceEdit();
    edit.insert(doc.uri, new vscode.Position(0, 0), "// dirty\n");
    await vscode.workspace.applyEdit(edit);
    assert.strictEqual(doc.isDirty, true, "precondition: the document must be dirty after an unsaved edit");

    const fsProvider = new GxFileSystemProvider();

    (shadowService as any).invalidateCache = () => {};

    let hydrateCalled = false;
    (shadowService as any).hydrateOpenedFile = async () => {
      hydrateCalled = true;
      return true;
    };

    let fireFileChangeCalled = false;
    (fsProvider as any).fireFileChange = () => {
      fireFileChangeCalled = true;
    };

    const syncManager = new SyncManager({ subscriptions: [] } as any, fsProvider, shadowService);
    await (syncManager as any).handleUpdateNotification("genexus://objects/DebugGravarDirty");

    assert.strictEqual(hydrateCalled, false, "hydrateOpenedFile must NOT be invoked for a dirty document");
    assert.strictEqual(fireFileChangeCalled, false, "fireFileChange must NOT be invoked for a dirty document");
  });
});
