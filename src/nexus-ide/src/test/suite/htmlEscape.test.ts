import * as assert from "assert";
import { escapeHtml } from "../../utils/htmlEscape";

suite("htmlEscape", () => {
  test("escapes tag-injection payload with no raw < or >", () => {
    const out = escapeHtml('<img src=x onerror=alert(1)>');
    assert.ok(!/</.test(out), "expected no raw < in output");
    assert.ok(!/>/.test(out), "expected no raw > in output");
    assert.ok(/&lt;/.test(out), "expected &lt; entity in output");
  });

  test("escapes double-quote attribute-breakout payload", () => {
    const out = escapeHtml('" onload="x');
    assert.ok(!/"/.test(out), "expected no raw double-quote in output");
  });

  test("escapes single-quote attribute-breakout payload", () => {
    const out = escapeHtml("' onclick='x");
    assert.ok(!/'/.test(out), "expected no raw single-quote in output");
  });

  test("escapes ampersand exactly once (no double-escaping)", () => {
    assert.strictEqual(escapeHtml("a & b"), "a &amp; b");
  });

  test("coerces null/undefined to empty string", () => {
    assert.strictEqual(escapeHtml(null), "");
    assert.strictEqual(escapeHtml(undefined), "");
  });

  test("coerces non-string values via String()", () => {
    assert.strictEqual(escapeHtml(42), "42");
  });
});
