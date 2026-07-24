import * as assert from "assert";
import { getObjectVariables } from "../../gxMemberResolver";
import { GxFileSystemProvider } from "../../gxFileSystem";

suite("getObjectVariables - TTL cache", () => {
  test("cache hit within TTL: readObjectVariables called once, both calls return the same data", async () => {
    const fsProvider = new GxFileSystemProvider();
    let callCount = 0;
    (fsProvider as any).readObjectVariables = async () => {
      callCount++;
      return [{ name: "cliente", type: "SDTCliente", length: 0 }];
    };

    const cache = new Map<string, any[]>();
    const first = await getObjectVariables(fsProvider, "MyObject", cache);
    const second = await getObjectVariables(fsProvider, "MyObject", cache);

    assert.strictEqual(callCount, 1, "expected a single fetch within the TTL window");
    assert.deepStrictEqual(first, second);
  });

  test("re-fetchable after cache eviction (proves the entry is not a permanent freeze)", async () => {
    const fsProvider = new GxFileSystemProvider();
    let callCount = 0;
    (fsProvider as any).readObjectVariables = async () => {
      callCount++;
      return [{ name: "x", type: "Character", length: callCount }];
    };

    const cache = new Map<string, any[]>();
    const first = await getObjectVariables(fsProvider, "MyObject", cache);
    assert.strictEqual(callCount, 1);

    // Simulate the caller-owned cache being evicted (e.g. by a future
    // invalidation hook); the TTL machinery must not resurrect stale data
    // and must allow a clean refetch.
    cache.delete("MyObject");
    const second = await getObjectVariables(fsProvider, "MyObject", cache);

    assert.strictEqual(callCount, 2, "expected a refetch after eviction");
    assert.notDeepStrictEqual(first, second);
    // TTL itself is time-based (30s, mirroring hoverProvider.ts's established
    // pattern) and is not independently re-verified here with a fake clock.
  });

  test("empty/non-array result returns [] and does not poison the cache", async () => {
    const fsProvider = new GxFileSystemProvider();
    let callCount = 0;
    (fsProvider as any).readObjectVariables = async () => {
      callCount++;
      return null;
    };

    const cache = new Map<string, any[]>();
    const first = await getObjectVariables(fsProvider, "MyObject", cache);
    assert.deepStrictEqual(first, []);

    const second = await getObjectVariables(fsProvider, "MyObject", cache);
    assert.deepStrictEqual(second, []);
    assert.strictEqual(callCount, 2, "a null result must not be cached, so every call refetches");
  });
});
