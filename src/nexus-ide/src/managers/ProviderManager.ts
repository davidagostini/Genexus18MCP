import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxShadowService } from "../gxShadowService";
import { GxDocumentSymbolProvider } from "../symbolProvider";
import { GxDefinitionProvider } from "../definitionProvider";
import { GxHoverProvider } from "../hoverProvider";
import { GxCompletionItemProvider } from "../completionProvider";
import { GxInlineCompletionItemProvider } from "../inlineCompletionProvider";
import { GxSignatureHelpProvider } from "../signatureHelpProvider";
import { GxCodeActionProvider } from "../codeActionProvider";
import { GxRenameProvider } from "../renameProvider";
import { GxFormatProvider } from "../formatProvider";
import { GxWorkspaceSymbolProvider } from "../workspaceSymbolProvider";
import { GxCodeLensProvider } from "../codeLensProvider";
import { GxReferenceProvider } from "../referenceProvider";

export class ProviderManager {
  public historyProvider: any;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider,
    private readonly shadowService?: GxShadowService,
  ) {}

  register() {
    this.context.subscriptions.push(
      vscode.languages.registerDocumentSymbolProvider(
        "genexus",
        new GxDocumentSymbolProvider(),
      ),
      vscode.languages.registerDefinitionProvider(
        "genexus",
        new GxDefinitionProvider(this.provider),
      ),
      vscode.languages.registerHoverProvider(
        "genexus",
        new GxHoverProvider(this.provider),
      ),
      vscode.languages.registerCompletionItemProvider(
        "genexus",
        new GxCompletionItemProvider(this.provider, (uri) =>
          this.provider.getPart(uri),
        ),
        ".",
        "&",
        ":",
      ),
      vscode.languages.registerInlineCompletionItemProvider(
        "genexus",
        new GxInlineCompletionItemProvider(this.provider),
      ),
      vscode.languages.registerSignatureHelpProvider(
        "genexus",
        new GxSignatureHelpProvider(this.provider),
        "(",
        ",",
      ),
      vscode.languages.registerCodeActionsProvider(
        "genexus",
        new GxCodeActionProvider(),
        {
          providedCodeActionKinds: [GxCodeActionProvider.kind],
        },
      ),
      vscode.languages.registerRenameProvider(
        "genexus",
        new GxRenameProvider(this.provider, this.shadowService),
      ),
      vscode.languages.registerDocumentFormattingEditProvider(
        "genexus",
        new GxFormatProvider(this.provider),
      ),
      vscode.languages.registerWorkspaceSymbolProvider(
        new GxWorkspaceSymbolProvider(this.provider),
      ),
      vscode.languages.registerCodeLensProvider(
        "genexus",
        new GxCodeLensProvider(this.provider),
      ),
      vscode.languages.registerReferenceProvider(
        "genexus",
        new GxReferenceProvider(this.provider),
      ),
    );

    this.registerHistoryProvider();
  }

  private registerHistoryProvider() {
    this.historyProvider = new (class
      implements vscode.TextDocumentContentProvider
    {
      private _data = new Map<string, string>();
      private _onDidChange = new vscode.EventEmitter<vscode.Uri>();
      readonly onDidChange = this._onDidChange.event;
      provideTextDocumentContent(uri: vscode.Uri): string {
        return this._data.get(uri.toString()) || "";
      }
      update(uri: vscode.Uri, content: string) {
        this._data.set(uri.toString(), content);
        this._onDidChange.fire(uri);
      }
      clear(uriPrefix: string) {
        for (const key of this._data.keys()) {
          if (key.includes(uriPrefix)) this._data.delete(key);
        }
      }
    })();
    this.context.subscriptions.push(
      vscode.workspace.registerTextDocumentContentProvider(
        "gx-history",
        this.historyProvider,
      ),
    );
  }
}
