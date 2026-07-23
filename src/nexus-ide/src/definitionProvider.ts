import * as vscode from "vscode";
import { GxFileSystemProvider } from "./gxFileSystem";
import { GxUriParser } from "./utils/GxUriParser";
import { Logger } from "./utils/Logger";

export class GxDefinitionProvider implements vscode.DefinitionProvider {
  private _cache = new Map<
    string,
    { definition: vscode.Definition; expires: number }
  >();
  private readonly CACHE_TTL = 60000; // 1 minute

  constructor(private readonly provider: GxFileSystemProvider) {}

  async provideDefinition(
    document: vscode.TextDocument,
    position: vscode.Position,
    _token: vscode.CancellationToken,
  ): Promise<vscode.Definition | undefined> {
    const line = document.lineAt(position.line).text;
    const range = document.getWordRangeAtPosition(position);
    if (!range) return undefined;

    const word = document.getText(range);

    // 0. Cache Check
    const cacheKey = word.toLowerCase();
    const cached = this._cache.get(cacheKey);
    if (cached && cached.expires > Date.now()) return cached.definition;

    // 1. Check if it's a Subroutine call: do 'SubName' or do SubName
    const doMatch = line.match(/\bdo\s+['"]?([a-zA-Z0-9_]+)['"]?/i);
    if (doMatch && line.includes(word) && word === doMatch[1]) {
      const subName = doMatch[1];
      const text = document.getText();
      const subDefRegex = new RegExp(`\\b(sub)\\s+['"]?${subName}['"]?`, "gi");
      let match;
      while ((match = subDefRegex.exec(text)) !== null) {
        const startPos = document.positionAt(match.index);
        return new vscode.Location(document.uri, startPos);
      }
    }

    // 2. KB Object Search (Remote)
    try {
      const result = await this.provider.queryObjects(word, 10, 15000);

      if (result && result.results && result.results.length > 0) {
        // Find exact match first
        const exactMatch = result.results.find(
          (obj: any) => obj.name.toLowerCase() === word.toLowerCase(),
        );
        if (exactMatch) {
          const definition = new vscode.Location(
            GxUriParser.toEditorUri(exactMatch.type, exactMatch.name),
            new vscode.Position(0, 0),
          );
          this._cache.set(cacheKey, {
            definition,
            expires: Date.now() + this.CACHE_TTL,
          });
          return definition;
        }
      }
    } catch (e) {
      Logger.error(`[Nexus IDE] Definition error: ${e}`);
    }

    return undefined;
  }
}
