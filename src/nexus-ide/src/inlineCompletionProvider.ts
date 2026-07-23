import * as vscode from "vscode";
import { Logger } from "./utils/Logger";
import { GxFileSystemProvider } from "./gxFileSystem";
import { GxUriParser } from "./utils/GxUriParser";
import { resolveVariableMembers } from "./gxMemberResolver";

const AI_TIMEOUT_MS = 4000;
const AI_CONTEXT_LINES = 20;

export class GxInlineCompletionItemProvider
  implements vscode.InlineCompletionItemProvider
{
  private varCache = new Map<string, any[]>();

  constructor(private readonly provider?: GxFileSystemProvider) {}

  async provideInlineCompletionItems(
    document: vscode.TextDocument,
    position: vscode.Position,
    _context: vscode.InlineCompletionContext,
    token: vscode.CancellationToken,
  ): Promise<vscode.InlineCompletionItem[] | vscode.InlineCompletionList> {
    if (!this.provider || token.isCancellationRequested) return [];

    const lineText = document.lineAt(position).text;
    const lineUntilCursor = lineText.substring(0, position.character);

    // 1. &var. member ghost text: real members resolved via the SDK, reusing
    // the same resolution the completion provider uses. Unknown variable ->
    // nothing, never a hardcoded guess.
    const dotMatch = lineUntilCursor.match(/&([a-zA-Z0-9_]+)\.$/);
    if (dotMatch) {
      return this.resolveMemberGhostText(document, dotMatch[1], position, token);
    }

    // 2. Optional AI free-form completion (opt-in, degrades to nothing).
    return this.resolveAiGhostText(document, position, lineUntilCursor, token);
  }

  private async resolveMemberGhostText(
    document: vscode.TextDocument,
    varName: string,
    position: vscode.Position,
    token: vscode.CancellationToken,
  ): Promise<vscode.InlineCompletionItem[]> {
    const objName = GxUriParser.getObjectName(document.uri);
    let resolved;
    try {
      resolved = await resolveVariableMembers(
        this.provider!,
        objName,
        varName,
        "",
        this.varCache,
      );
    } catch (e) {
      Logger.debug(`[Nexus IDE] Inline completion member resolution failed: ${e}`);
      return [];
    }

    if (token.isCancellationRequested || !resolved) return [];

    const range = new vscode.Range(position, position);
    const items: vscode.InlineCompletionItem[] = [];
    for (const field of resolved.fields) {
      items.push(new vscode.InlineCompletionItem(field.name, range));
    }
    for (const m of resolved.methods) {
      items.push(new vscode.InlineCompletionItem(`${m.name}()`, range));
    }
    return items;
  }

  private async resolveAiGhostText(
    document: vscode.TextDocument,
    position: vscode.Position,
    lineUntilCursor: string,
    token: vscode.CancellationToken,
  ): Promise<vscode.InlineCompletionItem[]> {
    const aiEnabled = vscode.workspace
      .getConfiguration("genexus")
      .get<boolean>("inlineCompletion.ai", false);
    if (!aiEnabled || !lineUntilCursor.trim()) return [];

    const startLine = Math.max(0, position.line - AI_CONTEXT_LINES);
    const context = document.getText(
      new vscode.Range(new vscode.Position(startLine, 0), position),
    );

    try {
      const result = await Promise.race([
        this.provider!.callMcpTool("genexus_ai_complete", { context }),
        new Promise<undefined>((resolve) =>
          setTimeout(() => resolve(undefined), AI_TIMEOUT_MS),
        ),
      ]);

      if (
        token.isCancellationRequested ||
        !result ||
        result.code === "AiEndpointNotConfigured" ||
        !result.completion
      ) {
        return [];
      }

      return [
        new vscode.InlineCompletionItem(
          String(result.completion),
          new vscode.Range(position, position),
        ),
      ];
    } catch (e) {
      Logger.debug(`[Nexus IDE] AI inline completion unavailable: ${e}`);
      return [];
    }
  }
}
