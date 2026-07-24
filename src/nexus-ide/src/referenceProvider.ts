import * as vscode from "vscode";
import { GxFileSystemProvider, TYPE_SUFFIX } from "./gxFileSystem";
import { GxUriParser } from "./utils/GxUriParser";
import { Logger } from "./utils/Logger";
import { isVariableToken, stripVariablePrefix } from "./utils/GxVariableToken";

// Cap on how many `usedby:` hits we deep-scan for a real Range. Objects beyond the cap
// still get a Location (object-level, (0,0)) so they aren't lost, but their source isn't
// fetched/scanned — that would mean fetching potentially hundreds of objects' source.
const MAX_DEEP_SCAN = 50;

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

export class GxReferenceProvider implements vscode.ReferenceProvider {
  constructor(private readonly provider: GxFileSystemProvider) {}

  async provideReferences(
    document: vscode.TextDocument,
    position: vscode.Position,
    context: vscode.ReferenceContext,
    _token: vscode.CancellationToken,
  ): Promise<vscode.Location[]> {
    if (!GxUriParser.isGeneXusUri(document.uri)) return [];

    const range = document.getWordRangeAtPosition(position);
    if (!range) return [];

    const word = document.getText(range);
    const isVariable = isVariableToken(document, range, word);

    // Variables are local to the object; we don't have a cross-KB variable search yet, so
    // scan the currently open document's text for real occurrences instead of the (wrong)
    // KB-wide `usedby:` lookup the old code ran against the stripped name.
    if (isVariable) {
      return this.findVariableReferencesInDocument(
        document, stripVariablePrefix(word), context.includeDeclaration,
      );
    }

    const targetName = word;

    // The attribute `usedby:` path has no single provider-known declaration site (results
    // span the whole KB), so `context.includeDeclaration` isn't actionable here.
    try {
      const results = await this.provider.queryObjects(`usedby:${targetName}`, 100, 15000);
      if (!results || !results.results) return [];

      const objects: any[] = results.results;
      const toScan = objects.slice(0, MAX_DEEP_SCAN);
      const overflow = objects.slice(MAX_DEEP_SCAN);

      if (overflow.length > 0) {
        Logger.info(
          `[Nexus IDE] References for '${targetName}': ${objects.length} object(s) found via usedby, deep-scanning only the first ${MAX_DEEP_SCAN}. ${overflow.length} object(s) reported at object-level location only.`,
        );
      }

      const locations: vscode.Location[] = [];
      for (const obj of toScan) {
        locations.push(...(await this.locateInObjectSource(obj, targetName)));
      }
      for (const obj of overflow) {
        locations.push(
          new vscode.Location(
            GxUriParser.toEditorUri(obj.type, obj.name),
            new vscode.Position(0, 0),
          ),
        );
      }

      return locations;
    } catch (e) {
      Logger.error(`[Nexus IDE] Reference Provider error for '${targetName}': ${e}`);
    }

    return [];
  }

  private findVariableReferencesInDocument(
    document: vscode.TextDocument,
    varName: string,
    includeDeclaration: boolean,
  ): vscode.Location[] {
    const text = document.getText();
    const regex = new RegExp(`&${escapeRegExp(varName)}\\b`, "gi");
    const locations: vscode.Location[] = [];

    let match: RegExpExecArray | null;
    while ((match = regex.exec(text)) !== null) {
      const start = document.positionAt(match.index);
      const end = document.positionAt(match.index + match[0].length);
      locations.push(new vscode.Location(document.uri, new vscode.Range(start, end)));
    }

    // The first occurrence in document order is treated as the declaration site (there's
    // no separate Variables-part declaration lookup here — see plan 067 maintenance notes).
    // Only exclude on an explicit `false`; an unset flag defaults to VS Code's own
    // "include declaration" behavior.
    if (includeDeclaration === false && locations.length > 0) {
      return locations.slice(1);
    }

    return locations;
  }

  private async locateInObjectSource(
    obj: any,
    targetName: string,
  ): Promise<vscode.Location[]> {
    const uri = GxUriParser.toEditorUri(obj.type, obj.name);
    const fallback = [new vscode.Location(uri, new vscode.Position(0, 0))];

    try {
      const target =
        obj.type && TYPE_SUFFIX[obj.type] ? `${obj.type}:${obj.name}` : obj.name;
      const result = await this.provider.callMcpTool(
        "genexus_read",
        { name: target, part: "Source" },
        15000,
      );

      if (!result || typeof result.source !== "string") {
        return fallback;
      }

      const source = result.isBase64
        ? Buffer.from(result.source, "base64").toString("utf8")
        : result.source;
      const lines = source.split(/\r?\n/);
      const lineRegex = new RegExp(`\\b${escapeRegExp(targetName)}\\b`, "gi");
      const locations: vscode.Location[] = [];

      lines.forEach((lineText: string, lineIndex: number) => {
        lineRegex.lastIndex = 0;
        let match: RegExpExecArray | null;
        while ((match = lineRegex.exec(lineText)) !== null) {
          const start = new vscode.Position(lineIndex, match.index);
          const end = new vscode.Position(lineIndex, match.index + match[0].length);
          locations.push(new vscode.Location(uri, new vscode.Range(start, end)));
        }
      });

      return locations.length > 0 ? locations : fallback;
    } catch (e) {
      Logger.warn(
        `[Nexus IDE] Failed to fetch source for ${obj.type}:${obj.name} while resolving references to '${targetName}': ${e}`,
      );
      return fallback;
    }
  }
}
