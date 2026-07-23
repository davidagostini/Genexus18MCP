import * as crypto from "crypto";
import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxUriParser } from "../utils/GxUriParser";

function nonce(): string {
  return crypto.randomBytes(16).toString("base64");
}

export class LayoutView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  /**
   * Builds the CSP-hardened OUTER HTML for the layout webview. The SDK-authored
   * `sdkHtml` (the GeneXus Layout part's own full HTML document, unknown shape/origin)
   * is never assigned directly to `panel.webview.html` — it's contained in a `sandbox`ed
   * `<iframe srcdoc>` so it cannot reach `acquireVsCodeApi`, the extension's webview
   * message channel, or any `vscode-resource:` origin. Exported for testing.
   *
   * Sandbox level: `allow-scripts` only (no `allow-same-origin`, no `allow-popups`,
   * no `allow-forms`, no `allow-top-navigation`). This is the MOST permissive level
   * that still isolates the parent per the plan's STOP condition — the SDK layout may
   * rely on inline scripts to render (GeneXus HTML generators commonly emit them), but
   * without `allow-same-origin` the iframe gets a unique opaque origin, so its scripts
   * cannot read/write the parent DOM, call `acquireVsCodeApi`, or access `document.cookie`/
   * storage of the extension's origin. Follow-up: verify against a real KB layout that
   * this level renders correctly (see plan 060 STOP condition) and tighten further
   * (drop `allow-scripts` too) if the layout turns out to be static markup.
   */
  static buildHtml(webview: vscode.Webview, sdkHtml: string): string {
    const iframeNonce = nonce();
    // srcdoc value must be HTML-attribute-escaped, not just quoted.
    const escapedSrcdoc = sdkHtml
      .replace(/&/g, "&amp;")
      .replace(/"/g, "&quot;");
    return `
            <!DOCTYPE html>
            <html>
            <head>
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${iframeNonce}'; frame-src 'self'; child-src 'self';">
                <style nonce="${iframeNonce}">
                    html, body { margin: 0; padding: 0; height: 100%; }
                    .readonly-banner {
                        font-family: var(--vscode-font-family, sans-serif);
                        font-size: 12px;
                        padding: 4px 8px;
                        background: var(--vscode-editorWidget-background, #2d2d2d);
                        color: var(--vscode-descriptionForeground, #ccc);
                        border-bottom: 1px solid var(--vscode-widget-border, #454545);
                    }
                    iframe { width: 100%; height: calc(100% - 24px); border: none; display: block; }
                </style>
            </head>
            <body>
                <div class="readonly-banner">Read-only preview — edit the layout in the GeneXus IDE.</div>
                <iframe sandbox="allow-scripts" srcdoc="${escapedSrcdoc}"></iframe>
            </body>
            </html>
        `;
  }

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

    const panel = vscode.window.createWebviewPanel(
      "gxLayout",
      `${objName} Layout (read-only)`,
      vscode.ViewColumn.Beside,
      { enableScripts: true, localResourceRoots: [] }
    );

    this.panels.set(uriKey, panel);
    panel.onDidDispose(() => this.panels.delete(uriKey));

    panel.webview.html = "<h1>Carregando Layout...</h1>";

    try {
      const result = await provider.readMcpResource(
        `genexus://objects/${target}/part/Layout`,
        30000,
      );
      let sdkHtml: string | undefined;
      if (result && result.source) {
        sdkHtml = result.source;
      } else if (typeof result === "string") {
        sdkHtml = result;
      }

      if (sdkHtml) {
        panel.webview.html = LayoutView.buildHtml(panel.webview, sdkHtml);
      } else {
        panel.webview.html = "<h1>Erro ao carregar Layout</h1>";
      }
    } catch (e) {
      panel.webview.html = `<h1>Erro Crítico: ${e}</h1>`;
    }
  }
}
