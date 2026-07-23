import * as vscode from "vscode";
import { Logger } from "./utils/Logger";
import {
  nativeFunctions,
  keywords,
  dataTypes,
  ruleKeywords,
} from "./gxNativeFunctions";
import { GxFileSystemProvider } from "./gxFileSystem";
import { GxUriParser } from "./utils/GxUriParser";
import {
  getObjectVariables,
  resolveVariableMembers,
  extractStructureFields,
} from "./gxMemberResolver";

export class GxCompletionItemProvider implements vscode.CompletionItemProvider {
  private varCache = new Map<string, any[]>();

  constructor(
    private readonly provider: GxFileSystemProvider,
    private readonly getPart?: (uri: vscode.Uri) => string,
  ) {}

  async provideCompletionItems(
    document: vscode.TextDocument,
    position: vscode.Position,
    _token: vscode.CancellationToken,
    _context: vscode.CompletionContext,
  ): Promise<vscode.CompletionItem[] | vscode.CompletionList> {
    const items: vscode.CompletionItem[] = [];
    const part = this.getPart ? this.getPart(document.uri) : "Source";
    const lineText = document.lineAt(position).text;
    const lineUntilCursor = lineText.substring(0, position.character);

    Logger.info(`[GxCompletion] Part: ${part}, Line: "${lineText}"`);

    // --- PART-SPECIFIC PRIORITIZATION ---

    // 1. If in "Variables" part
    if (part === "Variables") {
      const isAfterColon = lineUntilCursor.includes(":");
      if (isAfterColon) {
        // Suggest Data Types
        for (const type of dataTypes) {
          const item = new vscode.CompletionItem(
            type.name,
            vscode.CompletionItemKind.Struct,
          );
          item.detail = type.description;
          item.insertText = new vscode.SnippetString(type.snippet || type.name);
          item.sortText = `000_${type.name}`;
          items.push(item);
        }

        // Suggest SDTs/Transactions (fetch via Search if word at position is long enough)
        const word = document.getText(
          document.getWordRangeAtPosition(position),
        );
        if (word && word.length >= 2) {
          try {
            const sdtResults = await this.provider.queryObjects(
              `(type:SDT or type:Transaction or type:Domain) ${word}`,
              15,
              15000,
            );
            if (sdtResults && sdtResults.results) {
              for (const res of sdtResults.results) {
                const item = new vscode.CompletionItem(
                  res.name,
                  vscode.CompletionItemKind.Class,
                );
                item.detail = `(External Type) ${res.type}`;
                item.sortText = `005_${res.name}`;
                items.push(item);
              }
            }
          } catch (e) {
            Logger.debug(`[Nexus IDE] SDT/Transaction/Domain lookup failed for "${word}": ${e}`);
          }
        }
      } else {
        // Suggest common variable prefixes or names if applicable
        // (Usually not needed as users define new variables here)
      }
      if (items.length > 0) return items;
    }

    // 2. If in "Rules" part
    if (part === "Rules") {
      for (const rule of ruleKeywords) {
        const item = new vscode.CompletionItem(
          rule.name,
          vscode.CompletionItemKind.Keyword,
        );
        item.detail = rule.description;
        item.insertText = new vscode.SnippetString(rule.snippet || rule.name);
        item.sortText = `000_${rule.name}`;
        items.push(item);
      }
      // Also suggest variables/attributes as they are used in rules
      const objName = this.getObjName(document);
      const variables = await this.getVariables(objName);
      for (const v of variables) {
        const item = new vscode.CompletionItem(
          `&${v.name}`,
          vscode.CompletionItemKind.Variable,
        );
        item.detail = `${v.type}(${v.length})`;
        item.sortText = `010_${v.name}`;
        items.push(item);
      }
      // Attributes will be handled by step 5 below
    }

    // 1. Check for Member Access (e.g., &var. or &var.pa)
    const memberMatch = lineUntilCursor.match(
      /&([a-zA-Z0-9_]+)\.([a-zA-Z0-9_]*)$/,
    );
    if (memberMatch) {
      const varName = memberMatch[1];
      const partial = memberMatch[2];
      const objName = this.getObjName(document);
      const resolved = await resolveVariableMembers(
        this.provider,
        objName,
        varName,
        partial,
        this.varCache,
      );

      if (resolved) {
        // SDT / Transaction Structure Completion (fields first)
        for (const field of resolved.fields) {
          const item = new vscode.CompletionItem(
            field.name,
            vscode.CompletionItemKind.Field,
          );
          item.detail = `(Field) ${field.type}${field.isCollection ? " Collection" : ""}`;
          item.sortText = `000_${field.name}`; // Fields first
          items.push(item);
        }

        // Standard Methods
        for (const m of resolved.methods) {
          const item = new vscode.CompletionItem(
            m.name,
            vscode.CompletionItemKind.Method,
          );
          item.detail = `${m.name}${m.parameters}: ${m.returnType}`;
          item.documentation = new vscode.MarkdownString(m.description);
          item.insertText = new vscode.SnippetString(m.snippet || m.name);
          item.sortText = `002_${m.name}`; // Methods after fields
          items.push(item);
        }
      }
      return items;
    }

    // 2. Context Detection: Are we inside a For Each? Detection of Base Table.
    let isInsideForEach = false;
    let baseTable: string | undefined;
    for (let i = position.line; i >= 0; i--) {
      const line = document.lineAt(i).text;
      const forEachMatch = line.match(/for each\s+([a-zA-Z0-9_]+)/i);
      if (forEachMatch) {
        isInsideForEach = true;
        baseTable = forEachMatch[1];
        break;
      }
      if (line.toLowerCase().includes("for each")) {
        isInsideForEach = true;
        break;
      }
      if (line.toLowerCase().includes("endfor") && i !== position.line) break;
    }

    // 3. Native Functions and Keywords
    for (const func of nativeFunctions) {
      const item = new vscode.CompletionItem(
        func.name,
        vscode.CompletionItemKind.Function,
      );
      item.detail = `(Native) ${func.name}${func.parameters}`;
      item.documentation = new vscode.MarkdownString(
        `${func.description}\n\n**Example:** \`${func.example}\``,
      );
      item.insertText = new vscode.SnippetString(func.snippet || func.name);
      item.sortText = `003_${func.name}`;
      items.push(item);
    }

    for (const kw of keywords) {
      const item = new vscode.CompletionItem(
        kw.name,
        vscode.CompletionItemKind.Snippet,
      );
      item.insertText = new vscode.SnippetString(kw.snippet);
      items.push(item);
    }

    // 4. Local Variables
    const objName = this.getObjName(document);
    const variables = await this.getVariables(objName);
    for (const v of variables) {
      const item = new vscode.CompletionItem(
        `&${v.name}`,
        vscode.CompletionItemKind.Variable,
      );
      item.detail = `${v.type}(${v.length})`;
      item.sortText = `001_${v.name}`; // Variables after fields
      items.push(item);
    }

    // 5. Attributes (Prioritize if in For Each)
    const range = document.getWordRangeAtPosition(position);
    if (range || isInsideForEach) {
      const word = range ? document.getText(range) : "";
      // If in For Each, we search even with 0 chars or small prefix
      if (isInsideForEach || (word.length >= 2 && !word.startsWith("&"))) {
        const attrResults = await this.provider.queryObjects(
          `type:Attribute ${word}`,
          isInsideForEach ? 30 : 15,
          15000,
        );
        if (attrResults && attrResults.results) {
          for (const attr of attrResults.results) {
            const item = new vscode.CompletionItem(
              attr.name,
              vscode.CompletionItemKind.Property,
            );
            const typeInfo = `${attr.dataType}(${attr.length}${attr.decimals > 0 ? "," + attr.decimals : ""})`;
            item.detail = `(Attribute) ${typeInfo}`;

            const doc = new vscode.MarkdownString();
            if (attr.description)
              doc.appendMarkdown(`*${attr.description}*\n\n`);
            if (attr.table)
              doc.appendMarkdown(`**Base Table:** ${attr.table}\n\n`);
            item.documentation = doc;

            if (isInsideForEach) {
              item.preselect = true;
              // Even higher priority if it belongs to the detected base table
              if (
                baseTable &&
                attr.table?.toLowerCase() === baseTable.toLowerCase()
              ) {
                item.sortText = `000_000_${attr.name}`;
                item.detail = `(Base Attribute) ${typeInfo}`;
              } else {
                item.sortText = `000_001_${attr.name}`;
              }
            }
            items.push(item);
          }
        }

        // Extra: Fetch attributes directly from base table if search didn't catch them
        if (
          baseTable &&
          items.filter((it) => it.detail?.includes("(Base Attribute)"))
            .length === 0
        ) {
          try {
            const directAttrs = await this.provider.getStructure(
              baseTable,
              "get_visual",
              undefined,
              15000,
            );
            for (const attr of extractStructureFields(directAttrs)) {
              if (
                word &&
                !attr.name.toLowerCase().startsWith(word.toLowerCase())
              ) {
                continue;
              }
              const item = new vscode.CompletionItem(
                attr.name,
                vscode.CompletionItemKind.Property,
              );
              item.detail = `(Base Attribute) ${attr.type}`;
              item.sortText = `000_000_${attr.name}`;
              item.preselect = true;
              items.push(item);
            }
          } catch (e) {
            Logger.debug(`[Nexus IDE] Base table attribute lookup failed for "${baseTable}": ${e}`);
          }
        }
      }
    }

    return items;
  }

  private getObjName(document: vscode.TextDocument): string {
    return GxUriParser.getObjectName(document.uri);
  }

  private async getVariables(objName: string): Promise<any[]> {
    return getObjectVariables(this.provider, objName, this.varCache);
  }
}
