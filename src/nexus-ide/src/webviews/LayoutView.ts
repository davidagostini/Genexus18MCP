import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxUriParser } from "../utils/GxUriParser";

export class LayoutView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  static async show(uri: vscode.Uri, provider: GxFileSystemProvider) {
    const info = GxUriParser.parse(uri);
    if (!info) return;

    const { name: objName, type: typeStr } = info;
    const uriKey = uri.toString();
    const target = typeStr ? `${typeStr}:${objName}` : objName;

    if (this.panels.has(uriKey)) {
      this.panels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    // NOTE (plan 056): unlike the other webviews, `panel.webview.html` here is set
    // directly to `result.source` — a full HTML document rendered by the GeneXus SDK's
    // Layout part, not authored by this extension. We don't control its <head>/<script>
    // structure, so injecting a strict nonce-based CSP risks breaking whatever inline
    // styles/scripts the SDK emits. Left un-CSP'd per plan 056 STOP condition; scoped
    // follow-up: harden once the Layout HTML shape is confirmed safe to wrap.
    const panel = vscode.window.createWebviewPanel(
      "gxLayout",
      `${objName} Layout`,
      vscode.ViewColumn.Beside,
      { enableScripts: true, enableCommandUris: true, localResourceRoots: [] }
    );

    this.panels.set(uriKey, panel);
    panel.onDidDispose(() => this.panels.delete(uriKey));

    panel.webview.html = "<h1>Carregando Layout...</h1>";

    try {
      const result = await provider.readMcpResource(
        `genexus://objects/${target}/part/Layout`,
        30000,
      );
      if (result && result.source) {
        panel.webview.html = result.source;
      } else if (typeof result === "string") {
        panel.webview.html = result;
      } else {
        panel.webview.html = "<h1>Erro ao carregar Layout</h1>";
      }
    } catch (e) {
      panel.webview.html = `<h1>Erro Crítico: ${e}</h1>`;
    }
  }
}
