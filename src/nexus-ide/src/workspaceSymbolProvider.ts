import * as vscode from "vscode";
import { GxFileSystemProvider } from "./gxFileSystem";
import { GxUriParser } from "./utils/GxUriParser";
import { Logger } from "./utils/Logger";

export class GxWorkspaceSymbolProvider
  implements vscode.WorkspaceSymbolProvider
{
  constructor(private readonly provider: GxFileSystemProvider) {}

  async provideWorkspaceSymbols(
    query: string,
    _token: vscode.CancellationToken,
  ): Promise<vscode.SymbolInformation[]> {
    if (query.length < 2) return [];

    try {
      const results = await this.provider.queryObjects(query, 50, 15000);

      if (results && results.results) {
        return results.results.map((obj: any) => {
          const uri = GxUriParser.toEditorUri(obj.type, obj.name);

          let kind = vscode.SymbolKind.Object;
          if (obj.type === "Attribute") kind = vscode.SymbolKind.Property;
          else if (obj.type === "Procedure") kind = vscode.SymbolKind.Function;
          else if (obj.type === "Transaction") kind = vscode.SymbolKind.Class;
          else if (obj.type === "Folder" || obj.type === "Module")
            kind = vscode.SymbolKind.Module;

          return new vscode.SymbolInformation(
            obj.name,
            kind,
            obj.parent || "",
            new vscode.Location(uri, new vscode.Position(0, 0)),
          );
        });
      }
    } catch (e) {
      Logger.error(`[Nexus IDE] Workspace Symbol error: ${e}`);
    }

    return [];
  }
}
