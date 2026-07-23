import * as vscode from 'vscode';
import { nativeFunctions } from './gxNativeFunctions';
import { GxUriParser } from './utils/GxUriParser';
import { Logger } from './utils/Logger';
import { GxFileSystemProvider } from './gxFileSystem';

export class GxHoverProvider implements vscode.HoverProvider {
    private _cache = new Map<string, { hover: vscode.Hover, expires: number }>();
    private readonly CACHE_TTL = 30000; // 30 seconds

    constructor(private readonly provider: GxFileSystemProvider) {}

    async provideHover(
        document: vscode.TextDocument,
        position: vscode.Position,
        _token: vscode.CancellationToken
    ): Promise<vscode.Hover | undefined> {
        const range = document.getWordRangeAtPosition(position);
        if (!range) return undefined;
        const word = document.getText(range);
        
        // 0. Cache Check
        const cacheKey = `${document.uri.toString()}:${word}`;
        const cached = this._cache.get(cacheKey);
        if (cached && cached.expires > Date.now()) return cached.hover;

        // Cleanup expired cache
        if (this._cache.size > 100) {
            for (const [k, v] of this._cache) if (v.expires < Date.now()) this._cache.delete(k);
        }

        const lineText = document.lineAt(position.line).text;
        const commentIdx = lineText.indexOf('//');
        if (commentIdx !== -1 && position.character >= commentIdx) {
            return undefined;
        }

        const blockCommentStart = lineText.indexOf('/*');
        if (blockCommentStart !== -1 && position.character >= blockCommentStart) {
            // Simplified block comment detection for the same line
            const blockCommentEnd = lineText.indexOf('*/', blockCommentStart);
            if (blockCommentEnd === -1 || position.character <= blockCommentEnd + 1) {
                return undefined;
            }
        }

        // Filter out keywords or very small words to avoid noise
        if (word.length < 3) return undefined;

        let hover: vscode.Hover | undefined;

        // 1. Check Native Functions first (Fast, Local)
        const nativeFunc = nativeFunctions.find(f => f.name.toLowerCase() === word.toLowerCase());
        if (nativeFunc) {
            const markdown = new vscode.MarkdownString();
            markdown.appendMarkdown(`### (Native) **${nativeFunc.name}**\n\n`);
            markdown.appendMarkdown(`${nativeFunc.description}\n\n`);
            markdown.appendMarkdown(`**Syntax:** \`${nativeFunc.name}${nativeFunc.parameters}\`\n\n`);
            markdown.appendMarkdown(`**Returns:** \`${nativeFunc.returnType}\`\n\n`);
            if (nativeFunc.example) {
                markdown.appendMarkdown(`---\n**Example:**\n`);
                markdown.appendCodeblock(nativeFunc.example, 'genexus');
            }
            hover = new vscode.Hover(markdown);
        }

        // 2. Check for Local Variables (starts with &)
        if (!hover && word.startsWith('&')) {
            const varName = word.substring(1).toLowerCase();
            try {
                const objName = GxUriParser.getObjectName(document.uri);
                const variables = await this.provider.readObjectVariables(objName, 15000);
                
                if (Array.isArray(variables)) {
                    const variable = variables.find(v => v.name.toLowerCase() === varName);
                    if (variable) {
                        const markdown = new vscode.MarkdownString();
                        markdown.appendMarkdown(`### (Variable) **&${variable.name}**\n\n`);
                        markdown.appendMarkdown(`**Type:** \`${variable.type}\`\n\n`);
                        hover = new vscode.Hover(markdown);
                    }
                }
            } catch (e) {
                Logger.error(`[Nexus IDE] Error fetching variable hover: ${e}`);
            }
        }

        if (!hover) {
            try {
                // 3. Check if word is an Attribute (using specific Read:GetAttribute for rich data)
                const attrData = await this.provider.readAttributeMetadata(word, 15000);

                if (attrData && !attrData.error) {
                    const markdown = new vscode.MarkdownString();
                    markdown.appendMarkdown(`### (Attribute) **${attrData.name}**\n\n`);
                    if (attrData.description) markdown.appendMarkdown(`*${attrData.description}*\n\n`);
                    markdown.appendMarkdown(`**Type:** \`${attrData.type}(${attrData.length}${attrData.decimals > 0 ? ',' + attrData.decimals : ''})\`\n\n`);
                    if (attrData.table) {
                        markdown.appendMarkdown(`**Base Table:** [${attrData.table}](https://genexus.com/search?q=${attrData.table})\n\n`);
                    }
                    hover = new vscode.Hover(markdown);
                }

                // 4. Fallback: Search for the object in the index (Remote)
                if (!hover) {
                    const result = await this.provider.queryObjects(word, 1, 15000);

                    if (result && result.results && result.results.length > 0) {
                        const obj = result.results[0];
                        if (obj.name.toLowerCase() === word.toLowerCase()) {
                            const markdown = new vscode.MarkdownString();
                            markdown.appendMarkdown(`### [${obj.type}] **${obj.name}**\n\n`);
                            if (obj.description) markdown.appendMarkdown(`*${obj.description}*\n\n`);
                            if (obj.parm) {
                                markdown.appendCodeblock(obj.parm, 'genexus');
                            }
                            if (obj.snippet) {
                                markdown.appendMarkdown(`---\n**Preview:**\n`);
                                markdown.appendCodeblock(obj.snippet, 'genexus');
                            }
                            hover = new vscode.Hover(markdown);
                        }
                    }
                }
            } catch (e) {
                Logger.error(`[Nexus IDE] Hover error: ${e}`);
            }
        }

        if (hover) {
            this._cache.set(cacheKey, { hover, expires: Date.now() + this.CACHE_TTL });
        }
        return hover;
    }
}
