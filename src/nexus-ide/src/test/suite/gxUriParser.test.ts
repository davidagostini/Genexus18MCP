import * as assert from "assert";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { GxUriParser } from "../../utils/GxUriParser";
import { Logger } from "../../utils/Logger";

suite("GxUriParser.loadMirrorIndex corrupt-index handling", () => {
  class FakeSink {
    lines: string[] = [];
    appendLine(value: string): void {
      this.lines.push(value);
    }
  }

  let shadowRoot: string;

  setup(() => {
    shadowRoot = fs.mkdtempSync(path.join(os.tmpdir(), "gx-uri-parser-test-"));
  });

  teardown(() => {
    GxUriParser.configureShadowRoot(undefined);
    GxUriParser.clearMirrorIndex();
    fs.rmSync(shadowRoot, { recursive: true, force: true });
  });

  test("logs a warning and clears the index when .gx_index.json is corrupt", () => {
    const indexPath = path.join(shadowRoot, ".gx_index.json");
    fs.writeFileSync(indexPath, "{ this is not valid json");

    const sink = new FakeSink();
    Logger.configureForTest(sink, "debug");

    GxUriParser.configureShadowRoot(shadowRoot);
    GxUriParser.loadMirrorIndex();

    const warnLine = sink.lines.find((line) => line.includes("[WARN]"));
    assert.ok(warnLine, "expected a [WARN] line to be logged for the corrupt index");
    assert.ok(warnLine!.includes(indexPath), "warning should mention the corrupt index path");

    assert.strictEqual(GxUriParser.findMirrorPath("Transaction", "Anything"), null);
  });
});
