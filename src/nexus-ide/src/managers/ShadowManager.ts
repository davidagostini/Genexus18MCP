import * as vscode from "vscode";
import * as fs from "fs";
import { GxShadowService } from "../gxShadowService";
import { GxDiagnosticProvider } from "../diagnosticProvider";

import { GxFileSystemProvider } from "../gxFileSystem";
import { Logger } from "../utils/Logger";

export class ShadowManager {
  private watcher: vscode.FileSystemWatcher | undefined;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider,
    private readonly shadowService: GxShadowService,
    private readonly diagnosticProvider: GxDiagnosticProvider
  ) {}

  register() {
    const shadowRoot = this.shadowService.shadowRoot;
    if (!fs.existsSync(shadowRoot)) {
      fs.mkdirSync(shadowRoot, { recursive: true });
      Logger.info(`[ShadowManager] Created shadow root: ${shadowRoot}`);
    }

    this.watcher = vscode.workspace.createFileSystemWatcher(
      new vscode.RelativePattern(this.shadowService.shadowRoot, "**/*.gx")
    );

    this.watcher.onDidChange(async (uri) => {
      if (this.shadowService.shouldIgnore(uri.fsPath)) return;

      await this.shadowService.syncToKB(uri.fsPath);
      await this.diagnosticProvider.refreshAll();
    });

    this.watcher.onDidCreate(async (uri) => {
      if (this.shadowService.shouldIgnore(uri.fsPath)) return;
      await this.shadowService.syncToKB(uri.fsPath);
    });

    this.context.subscriptions.push(this.watcher);
  }

  dispose() {
    this.watcher?.dispose();
  }
}
