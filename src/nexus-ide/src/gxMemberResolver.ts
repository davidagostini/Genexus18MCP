import { GxFileSystemProvider } from "./gxFileSystem";
import { Logger } from "./utils/Logger";
import { typeMethods, GxMethod } from "./gxNativeFunctions";

export interface GxResolvedField {
  name: string;
  type: string;
  isCollection?: boolean;
}

export interface GxResolvedMembers {
  fields: GxResolvedField[];
  methods: GxMethod[];
}

/**
 * Extracts the visual-structure children (SDT/Transaction fields) returned by
 * `genexus_structure` into a flat, provider-agnostic shape.
 */
export function extractStructureFields(structure: any): GxResolvedField[] {
  const children = Array.isArray(structure?.children) ? structure.children : [];
  return children
    .filter((child: any) => child && typeof child.name === "string")
    .map((child: any) => ({
      name: child.name,
      type: child.type || "Unknown",
      isCollection: Boolean(child.isCollection),
    }));
}

const CACHE_TTL_MS = 30000; // mirror hoverProvider.ts:9
// Parallel expiry timestamps per caller-owned cache instance, so the exported
// signature (cache: Map<string, any[]>) stays byte-identical for callers.
const expiryByCache = new WeakMap<Map<string, any[]>, Map<string, number>>();

/**
 * Fetches (and caches) the declared variables of a GeneXus object.
 * `cache` is owned by the caller so each consumer (completion / inline
 * completion providers) keeps its own lifetime for the cached values.
 * Entries expire after `CACHE_TTL_MS` so KB edits (new/changed/renamed
 * variables) are reflected without a window reload.
 */
export async function getObjectVariables(
  provider: GxFileSystemProvider,
  objName: string,
  cache: Map<string, any[]>,
): Promise<any[]> {
  let expiries = expiryByCache.get(cache);
  if (!expiries) {
    expiries = new Map();
    expiryByCache.set(cache, expiries);
  }

  const now = Date.now();
  const exp = expiries.get(objName);
  if (cache.has(objName) && exp !== undefined && exp > now) {
    return cache.get(objName)!;
  }

  try {
    const result = await provider.readObjectVariables(objName, 15000);
    if (result && Array.isArray(result)) {
      cache.set(objName, result);
      expiries.set(objName, now + CACHE_TTL_MS);
      return result;
    }
  } catch (e) {
    Logger.error(`[Nexus IDE] Error fetching variables: ${e}`);
  }
  return [];
}

/**
 * Resolves the real members (structure fields + type methods) available on
 * `&varName.` inside `objName`, filtered by the text already typed after the
 * dot (`partial`). Returns `undefined` when the variable itself is unknown —
 * callers must treat that as "no suggestion", never a guess.
 */
export async function resolveVariableMembers(
  provider: GxFileSystemProvider,
  objName: string,
  varName: string,
  partial: string,
  cache: Map<string, any[]>,
): Promise<GxResolvedMembers | undefined> {
  const variables = await getObjectVariables(provider, objName, cache);
  const variable = variables.find(
    (v) => v.name.toLowerCase() === varName.toLowerCase(),
  );
  if (!variable) return undefined;

  let type = variable.type;
  const isCollection = type.endsWith("Collection");
  if (isCollection) type = "Collection";

  const fields: GxResolvedField[] = [];
  const isStructureType =
    !["Character", "Numeric", "Date", "DateTime", "Boolean", "Collection"].includes(type) &&
    !type.startsWith("Character") &&
    !type.startsWith("Numeric") &&
    !type.startsWith("VarChar");

  if (isStructureType) {
    try {
      const structure = await provider.getStructure(type, "get_visual", undefined, 15000);
      for (const field of extractStructureFields(structure)) {
        if (partial && !field.name.toLowerCase().startsWith(partial.toLowerCase())) continue;
        fields.push(field);
      }
    } catch (e) {
      Logger.error(`[Nexus IDE] SDT Structure error: ${e}`);
    }
  }

  const allMethods = typeMethods[type] || typeMethods["Character"];
  const methods = allMethods.filter(
    (m) => !partial || m.name.toLowerCase().startsWith(partial.toLowerCase()),
  );

  return { fields, methods };
}
