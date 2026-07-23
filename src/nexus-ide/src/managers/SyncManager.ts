import * as vscode from "vscode";
import * as http from "http";
import * as path from "path";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxShadowService } from "../gxShadowService";
import { Logger } from "../utils/Logger";

export class SyncManager {
  private _sseRequest?: http.ClientRequest;
  private _isDisposed = false;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider,
    private readonly shadowService: GxShadowService
  ) {}

  public register() {
    this.startListening();
    
    // Command to manually trigger sync/check
    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.syncWithKb", async () => {
        const editor = vscode.window.activeTextEditor;
        if (editor && editor.document.uri.scheme === "file" && editor.document.uri.fsPath.endsWith(".gx")) {
             await this.shadowService.hydrateOpenedFile(editor.document.uri, this.provider);
             vscode.window.showInformationMessage("Objeto sincronizado com a KB.");
        }
      })
    );
  }

  private startListening() {
    if (this._isDisposed) return;

    const url = new URL(this.provider.baseUrl);
    const options: http.RequestOptions = {
      hostname: url.hostname,
      port: url.port,
      path: url.pathname,
      method: "GET",
      headers: {
        "Accept": "text/event-stream",
        "Cache-Control": "no-cache",
        "Connection": "keep-alive"
      }
    };

    Logger.info(`[SyncManager] Connecting to SSE: ${this.provider.baseUrl}`);

    this._sseRequest = http.request(options, (res) => {
      let buffer = "";
      res.on("data", (chunk) => {
        buffer += chunk.toString();
        this.processSseBuffer(buffer);
        // Basic SSE line clear logic could go here if needed
      });

      res.on("end", () => {
        Logger.info("[SyncManager] SSE Connection ended. Reconnecting in 5s...");
        this.scheduleReconnect();
      });

      res.on("error", (err) => {
        Logger.error(`[SyncManager] SSE Error: ${err}`);
        this.scheduleReconnect();
      });
    });

    this._sseRequest.on("error", (err) => {
      Logger.error(`[SyncManager] SSE Request Error: ${err}`);
      this.scheduleReconnect();
    });

    this._sseRequest.end();
  }

  private processSseBuffer(buffer: string) {
    const lines = buffer.split("\n");
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (line.startsWith("data: ")) {
            try {
                const data = JSON.parse(line.substring(6));
                if (data.method === "notifications/resources/updated") {
                    this.handleUpdateNotification(data.params.uri); // Pass uri string directly
                }
            } catch (e) {
                // Not JSON or partial JSON, ignore
            }
        }
    }
  }

  private async handleUpdateNotification(uriStr: string) {
        const uri = vscode.Uri.parse(uriStr);
        const objectName = uri.path.split("/").pop() || "";

        if (!objectName) return;

        // Invalidate cache in ShadowService
        this.shadowService.invalidateCache(objectName);

        // Find ALL open documents that might match this object (including non-visible tabs)
        // We match by filename (e.g. DebugGravar.gx) to be safe
        const matchingDocs = vscode.workspace.textDocuments.filter(doc => {
            const fsPath = doc.uri.fsPath.toLowerCase();
            const fileName = path.basename(fsPath);
            return fileName.startsWith(objectName.toLowerCase() + ".") || fileName === objectName.toLowerCase() + ".gx";
        });

        if (matchingDocs.length > 0) {
            Logger.info(`[SyncManager] External change detected for ${objectName}. Refreshing ${matchingDocs.length} documents.`);
            vscode.window.setStatusBarMessage(`$(sync) KB Atualizada: ${objectName}`, 5000);
            
            // Wait a bit to ensure the OS/SDK has finished file operations
            await new Promise(resolve => setTimeout(resolve, 800));

            for (const doc of matchingDocs) {
                if (doc.isDirty) {
                    Logger.info(`[SyncManager] Skipping refresh for dirty document: ${objectName}`);
                    continue;
                }

                // Proactively hydrate to update .gx_mirror file on disk
                const success = await this.shadowService.hydrateOpenedFile(doc.uri, this.provider);
                if (success) {
                    // Invalidate internal provider caches and tell VS Code to reload the file natively
                    this.provider.fireFileChange(doc.uri);
                }
            }
        }
    }

  private scheduleReconnect() {
    if (this._isDisposed) return;
    setTimeout(() => this.startListening(), 5000);
  }

  public dispose() {
    this._isDisposed = true;
    this._sseRequest?.destroy();
  }
}
