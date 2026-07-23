import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { formatMcpErrorMessage } from "../utils/McpErrorFormatter";

export class PropertiesView {
  private static currentPanel: vscode.WebviewPanel | undefined;

  static async show(target: string, controlName: string | null, provider: GxFileSystemProvider) {
    const column = vscode.window.activeTextEditor ? vscode.window.activeTextEditor.viewColumn : undefined;

    if (this.currentPanel) {
      this.currentPanel.reveal(column);
    } else {
      this.currentPanel = vscode.window.createWebviewPanel(
        "gxProperties",
        "GX Properties",
        vscode.ViewColumn.Three,
        { enableScripts: true, localResourceRoots: [] }
      );
      this.currentPanel.onDidDispose(() => (this.currentPanel = undefined));
    }

    this.currentPanel.webview.html = this.getHtml(
      target,
      controlName,
      this.currentPanel.webview.cspSource,
    );

    try {
      const result = await provider.callMcpTool("genexus_properties", {
        action: "get",
        name: target,
        control: controlName
      });
      if (result && !result.error) {
        this.currentPanel.webview.postMessage({ type: "update", properties: result.properties });
      } else {
        this.currentPanel.webview.html = `<h1>${formatMcpErrorMessage("Properties Error:", result)}</h1>`;
      }

      this.currentPanel.webview.onDidReceiveMessage(async (message) => {
        if (message.command === "setProperty") {
          try {
            const result = await provider.callMcpTool("genexus_properties", {
              action: "set",
              name: target,
              propertyName: message.name,
              value: message.value,
              control: controlName
            });
            if (result?.error || result?.status === "Error") {
              throw result;
            }
          } catch (e) {
            vscode.window.showErrorMessage(formatMcpErrorMessage("Failed to set property:", e));
          }
        }
      });
    } catch (e) {
      this.currentPanel.webview.html = `<h1>${formatMcpErrorMessage("Critical Error:", e)}</h1>`;
    }
  }

  private static getHtml(target: string, controlName: string | null, cspSource: string): string {
    return `
      <!DOCTYPE html>
      <html>
      <head>
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${cspSource} 'unsafe-inline'; script-src ${cspSource} 'unsafe-inline';">
        <style>
          body { font-family: sans-serif; padding: 10px; background-color: var(--vscode-editor-background); color: var(--vscode-editor-foreground); }
          .prop-row { display: flex; padding: 4px 0; border-bottom: 1px solid #333; align-items: center; }
          .prop-name { flex: 1; font-weight: bold; font-size: 11px; }
          .prop-value { flex: 1; }
          input, select { width: 100%; background: #333; color: white; border: 1px solid #555; font-size: 11px; }
        </style>
      </head>
      <body>
        <h3>${controlName || target}</h3>
        <div id="props">Loading...</div>
        <script>
          const vscode = acquireVsCodeApi();
          window.addEventListener('message', event => {
            const data = event.data;
            if (data.type === 'update') {
              const container = document.getElementById('props');
              container.innerHTML = '';
              data.properties.forEach(p => {
                const row = document.createElement('div');
                row.className = 'prop-row';
                
                const name = document.createElement('div');
                name.className = 'prop-name';
                name.innerText = p.name;
                
                const valContainer = document.createElement('div');
                valContainer.className = 'prop-value';
                
                if (p.options) {
                  const select = document.createElement('select');
                  p.options.forEach(opt => {
                    const o = document.createElement('option');
                    o.value = opt;
                    o.innerText = opt;
                    if (opt === p.value) o.selected = true;
                    select.appendChild(o);
                  });
                  select.onchange = () => vscode.postMessage({ command: 'setProperty', name: p.name, value: select.value });
                  valContainer.appendChild(select);
                } else {
                  const input = document.createElement('input');
                  input.value = p.value;
                  input.onblur = () => vscode.postMessage({ command: 'setProperty', name: p.name, value: input.value });
                  valContainer.appendChild(input);
                }
                
                row.appendChild(name);
                row.appendChild(valContainer);
                container.appendChild(row);
              });
            }
          });
        </script>
      </body>
      </html>
    `;
  }
}
