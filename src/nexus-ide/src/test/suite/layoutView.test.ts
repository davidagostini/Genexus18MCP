import * as assert from "assert";
import * as vscode from "vscode";
import { LayoutView } from "../../webviews/LayoutView";

suite("LayoutView - CSP + sandboxed iframe containment", () => {
  test("generated OUTER HTML has a CSP meta and wraps the SDK HTML in a sandboxed iframe", () => {
    const panel = vscode.window.createWebviewPanel(
      "gxLayoutTest",
      "Layout Test",
      vscode.ViewColumn.Beside,
      { enableScripts: true },
    );

    try {
      const sdkHtml = "<html><body><h1>Layout &amp; \"quoted\" content</h1></body></html>";
      const html = LayoutView.buildHtml(panel.webview, sdkHtml);

      assert.ok(
        /Content-Security-Policy/.test(html),
        "expected a Content-Security-Policy meta tag in the generated OUTER HTML",
      );
      assert.ok(
        /<iframe[^>]*\bsandbox="[^"]*"/.test(html),
        "expected an <iframe> with a sandbox attribute",
      );
      assert.ok(/srcdoc="/.test(html), "expected the SDK HTML to be carried via srcdoc");
      assert.ok(
        !/allow-same-origin/.test(html),
        "expected sandbox to NOT grant allow-same-origin (containment)",
      );
      assert.ok(/read-only/i.test(html), "expected an honest read-only preview label");
    } finally {
      panel.dispose();
    }
  });
});
