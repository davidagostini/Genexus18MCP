import * as assert from "assert";
import { Logger } from "../../utils/Logger";

suite("Logger level gating", () => {
  class FakeSink {
    lines: string[] = [];
    appendLine(value: string): void {
      this.lines.push(value);
    }
  }

  test("debug is suppressed at info level", () => {
    const sink = new FakeSink();
    Logger.configureForTest(sink, "info");

    Logger.debug("should not appear");
    Logger.info("should appear");

    assert.strictEqual(sink.lines.length, 1);
    assert.ok(sink.lines[0].includes("[INFO] should appear"));
  });

  test("error is always emitted regardless of configured level", () => {
    const sink = new FakeSink();
    Logger.configureForTest(sink, "error");

    Logger.debug("suppressed");
    Logger.info("suppressed");
    Logger.warn("suppressed");
    Logger.error("always shown");

    assert.strictEqual(sink.lines.length, 1);
    assert.ok(sink.lines[0].includes("[ERROR] always shown"));
  });

  test("debug level lets every severity through", () => {
    const sink = new FakeSink();
    Logger.configureForTest(sink, "debug");

    Logger.error("e");
    Logger.warn("w");
    Logger.info("i");
    Logger.debug("d");

    assert.strictEqual(sink.lines.length, 4);
  });

  test("each emitted line carries an ISO timestamp prefix", () => {
    const sink = new FakeSink();
    Logger.configureForTest(sink, "debug");

    Logger.info("timestamped");

    assert.match(sink.lines[0], /^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] \[INFO\] timestamped$/);
  });
});
