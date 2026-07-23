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

/**
 * Fetches (and caches) the declared variables of a GeneXus object.
 * `cache` is owned by the caller so each consumer (completion / inline
 * completion providers) keeps its own lifetime for the cached values.
 */
export async function getObjectVariables(
  provider: GxFileSystemProvider,
  objName: string,
  cache: Map<string, any[]>,
): Promise<any[]> {
  if (cache.has(objName)) return cache.get(objName)!;

  try {
    const result = await provider.readObjectVariables(objName, 15000);
    if (result && Array.isArray(result)) {
      cache.set(objName, result);
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
