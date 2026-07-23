import * as assert from "assert";
import * as http from "http";
import { GxGatewayClient } from "../../infra/GxGatewayClient";

/**
 * Characterization tests for GxGatewayClient's pure parsing/unwrap helpers and
 * its session-init/retry logic against a real (local, in-process) HTTP server.
 *
 * These do NOT hit a real gateway: a throwaway http.Server stands in for the
 * transport so the retry/session behavior is exercised end-to-end without
 * network I/O leaving the machine.
 */
suite("GxGatewayClient", () => {
  function makeClient(baseUrl: string): GxGatewayClient {
    return new GxGatewayClient(baseUrl);
  }

  // --- Pure helpers (private methods reached via cast, no I/O) ---

  test("unwrapGatewayResponse parses nested JSON-in-JSON content blocks", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const body = JSON.stringify({
      result: { content: [{ type: "text", text: JSON.stringify({ ok: true, value: 42 }) }] },
    });
    const unwrapped = client.unwrapGatewayResponse(body);
    assert.deepStrictEqual(unwrapped, { ok: true, value: 42 });
  });

  test("unwrapGatewayResponse falls back to raw text when content is not JSON", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const body = JSON.stringify({
      result: { content: [{ type: "text", text: "plain text result" }] },
    });
    const unwrapped = client.unwrapGatewayResponse(body);
    assert.strictEqual(unwrapped, "plain text result");
  });

  test("unwrapGatewayResponse returns the result wrapper when there is no content list", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const body = JSON.stringify({ result: { tools: [{ name: "foo" }] } });
    const unwrapped = client.unwrapGatewayResponse(body);
    assert.deepStrictEqual(unwrapped, { tools: [{ name: "foo" }] });
  });

  test("unwrapGatewayResponse returns the full response when there is no result wrapper", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const body = JSON.stringify({ error: "boom" });
    const unwrapped = client.unwrapGatewayResponse(body);
    assert.deepStrictEqual(unwrapped, { error: "boom" });
  });

  test("unwrapGatewayResponse returns the raw body when it is not valid JSON", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const unwrapped = client.unwrapGatewayResponse("not json at all");
    assert.strictEqual(unwrapped, "not json at all");
  });

  test("isExpiredSessionResponse detects the expired-session error string", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    assert.strictEqual(
      client.isExpiredSessionResponse({ error: "Unknown or expired MCP session" }),
      true,
    );
    assert.strictEqual(client.isExpiredSessionResponse({ error: "some other error" }), false);
    assert.strictEqual(client.isExpiredSessionResponse(null), false);
    assert.strictEqual(client.isExpiredSessionResponse("string payload"), false);
  });

  test("isRetriableTransportError recognizes known transient transport failures", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    assert.strictEqual(client.isRetriableTransportError(new Error("ECONNRESET")), true);
    assert.strictEqual(client.isRetriableTransportError(new Error("socket hang up")), true);
    assert.strictEqual(
      client.isRetriableTransportError(new Error("connect ECONNREFUSED 127.0.0.1:5000")),
      true,
    );
    assert.strictEqual(
      client.isRetriableTransportError(new Error("Unknown or expired MCP session")),
      true,
    );
    assert.strictEqual(client.isRetriableTransportError(new Error("totally unrelated")), false);
  });

  test("describeCommand labels tool/resource/prompt calls distinctly", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    assert.strictEqual(
      client.describeCommand({ method: "tools/call", params: { name: "genexus_query" } }),
      "tool:genexus_query",
    );
    assert.strictEqual(
      client.describeCommand({ method: "resources/read", params: { uri: "gx://x" } }),
      "resource:gx://x",
    );
    assert.strictEqual(
      client.describeCommand({ method: "prompts/get", params: { name: "p1" } }),
      "prompt:p1",
    );
    assert.strictEqual(client.describeCommand({ method: "tools/list" }), "tools/list");
  });

  // --- Session-init / retry logic against a real local HTTP double ---

  test("initializeMcpSession stores the mcp-session-id header and reuses it", async () => {
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (c) => (body += c));
      req.on("end", () => {
        res.setHeader("mcp-session-id", "session-abc");
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify({ result: { ok: true } }));
      });
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);

      const sessionId = await client.initializeMcpSession(2000);
      assert.strictEqual(sessionId, "session-abc");

      // Second call must reuse the cached session id without another init round-trip.
      const sessionIdAgain = await client.initializeMcpSession(2000);
      assert.strictEqual(sessionIdAgain, "session-abc");
    } finally {
      server.close();
    }
  });

  test("initializeMcpSession throws when the gateway never returns a session id", async () => {
    const server = http.createServer((req, res) => {
      req.on("data", () => {});
      req.on("end", () => {
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify({ result: { ok: true } }));
      });
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);

      await assert.rejects(
        () => client.initializeMcpSession(2000),
        /MCP session was not established/,
      );
    } finally {
      server.close();
    }
  });

  test("callMcp retries and re-initializes the session on an expired-session error", async () => {
    let callCount = 0;
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (c) => (body += c));
      req.on("end", () => {
        const parsed = JSON.parse(body);
        res.setHeader("Content-Type", "application/json");

        if (parsed.method === "initialize") {
          res.setHeader("mcp-session-id", `session-${callCount}`);
          res.end(JSON.stringify({ result: { ok: true } }));
          return;
        }

        callCount++;
        if (callCount === 1) {
          // First real call: report the session as expired so the client retries.
          res.end(JSON.stringify({ result: { error: "unknown or expired mcp session" } }));
          return;
        }

        res.end(JSON.stringify({ result: { tools: ["genexus_query"] } }));
      });
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);

      const result = await client.callMcp("tools/list", undefined, 2000);
      assert.deepStrictEqual(result, { tools: ["genexus_query"] });
      assert.strictEqual(callCount, 2, "expected exactly one retry after the expired session");
    } finally {
      server.close();
    }
  });

  test("callMcp surfaces a timeout error when the gateway never responds", async () => {
    const server = http.createServer(() => {
      // Never respond; let the client's own timeout fire.
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);

      await assert.rejects(() => client.callMcp("tools/list", undefined, 300), /Timeout Gateway/);
    } finally {
      server.close();
    }
  });
});
