import * as crypto from "crypto";
import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxTreeItem } from "../gxTreeProvider";
import { GxUriParser } from "../utils/GxUriParser";

// Vendored from npm `mermaid@11.16.0` (node_modules/mermaid/dist/mermaid.min.js,
// https://www.npmjs.com/package/mermaid) into resources/mermaid.min.js so the
// diagram webview never fetches a script from a CDN. Re-vendor by bumping the
// devDependency in package.json and re-copying dist/mermaid.min.js here.
const MERMAID_ASSET = "mermaid.min.js";

function nonce(): string {
  return crypto.randomBytes(16).toString("base64");
}

export class DiagramView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  /** Builds the CSP-hardened HTML for the diagram webview. Exported for testing. */
  static buildHtml(
    webview: vscode.Webview,
    extensionUri: vscode.Uri,
    mermaidSource: string,
  ): string {
    const mermaidUri = webview.asWebviewUri(
      vscode.Uri.joinPath(extensionUri, "resources", MERMAID_ASSET),
    );
    const scriptNonce = nonce();
    return `
            <!DOCTYPE html>
            <html>
            <head>
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource} data:; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${scriptNonce}' ${webview.cspSource};">
                <script nonce="${scriptNonce}" src="${mermaidUri}"></script>
                <script nonce="${scriptNonce}">mermaid.initialize({ startOnLoad: true });</script>
            </head>
            <body>
                <pre class="mermaid">
                    ${mermaidSource}
                </pre>
            </body>
            </html>
        `;
  }

  static async show(
    item: GxTreeItem | undefined,
    provider: GxFileSystemProvider,
    extensionUri: vscode.Uri,
  ) {
    let objName = "";
    if (item && item.gxName) {
      objName = item.gxName;
    } else {
      const editor = vscode.window.activeTextEditor;
      let targetUri = editor?.document.uri;
      if (!targetUri || !GxUriParser.isGeneXusUri(targetUri)) {
        const visibleGxEditor = vscode.window.visibleTextEditors.find(
          (e) => GxUriParser.isGeneXusUri(e.document.uri)
        );
        if (visibleGxEditor) targetUri = visibleGxEditor.document.uri;
      }
      
      if (targetUri && GxUriParser.isGeneXusUri(targetUri)) {
        objName = GxUriParser.getObjectName(targetUri);
      }
    }

    if (!objName) {
      vscode.window.showErrorMessage("Selecione um objeto para gerar o diagrama.");
      return;
    }

    if (this.panels.has(objName)) {
        this.panels.get(objName)!.reveal(vscode.ViewColumn.Beside);
        return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxDiagram",
      `${objName} Diagram`,
      vscode.ViewColumn.Beside,
      {
        enableScripts: true,
        localResourceRoots: [vscode.Uri.joinPath(extensionUri, "resources")],
      }
    );

    this.panels.set(objName, panel);
    panel.onDidDispose(() => this.panels.delete(objName));

    panel.webview.html = `<h1>Gerando Diagrama para ${objName}...</h1>`;

    try {
      const result = await provider.callMcpTool("genexus_doc", {
        action: "visualize",
        target: objName,
      });

      if (result && result.mermaid) {
        panel.webview.html = DiagramView.buildHtml(panel.webview, extensionUri, result.mermaid);
      } else {
        panel.webview.html = "<h1>Não foi possível gerar o diagrama para este objeto.</h1>";
      }
    } catch (e) {
      panel.webview.html = `<h1>Erro: ${e}</h1>`;
    }
  }
}
