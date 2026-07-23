import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";
import * as os from "os";
import { GxFileSystemProvider } from "../gxFileSystem";
import { resolveGatewayHttpPort } from "../utils/GatewayConfig";
import { Logger } from "../utils/Logger";
import {
  CONFIG_SECTION,
  COMMAND_PREFIX,
  DISCOVERY_DELAY,
  STATE_KEY_MCP_DISCOVERY,
} from "../constants";

type DiscoverySnapshot = {
  tools: any[];
  resources: any[];
  resourceTemplates: any[];
  prompts: any[];
  fetchedAt: string;
};

/**
 * McpDiscoveryManager: Torna o servidor MCP GeneXus visivel para IAs.
 */
export class McpDiscoveryManager {
  private _toolRegistrations: vscode.Disposable[] = [];

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider,
  ) {}

  public async register() {
    const discovery = await this.loadDiscoverySnapshot();
    this.registerCopilotTools(discovery);

    if (this.context.extensionMode === vscode.ExtensionMode.Development) {
      this.deleteLocalDiscoveryFilesForDevelopment();
    } else {
      setTimeout(() => this.createLocalDiscoveryFile(), DISCOVERY_DELAY);
    }
    this.registerCommands();
  }

  public cleanupDevelopmentDiscoveryFiles() {
    if (this.context.extensionMode !== vscode.ExtensionMode.Development) {
      return;
    }

    this.deleteLocalDiscoveryFilesForDevelopment();
  }

  private getEffectivePort(): number {
    return resolveGatewayHttpPort(
      this.context.extensionPath,
      vscode.workspace.getConfiguration(CONFIG_SECTION),
    );
  }

  private async loadDiscoverySnapshot(): Promise<DiscoverySnapshot | null> {
    try {
      const [tools, resources, resourceTemplates, prompts] = await Promise.all([
        this.provider.listMcpTools(15000),
        this.provider.listMcpResources(15000),
        this.provider.listMcpResourceTemplates(15000),
        this.provider.listMcpPrompts(15000),
      ]);

      const snapshot: DiscoverySnapshot = {
        tools,
        resources,
        resourceTemplates,
        prompts,
        fetchedAt: new Date().toISOString(),
      };

      await this.context.globalState.update(STATE_KEY_MCP_DISCOVERY, snapshot);
      Logger.info(
        `[McpDiscoveryManager] MCP discovery loaded: ${tools.length} tools, ${resources.length} resources, ${resourceTemplates.length} templates, ${prompts.length} prompts.`,
      );
      return snapshot;
    } catch (e) {
      Logger.warn(`[McpDiscoveryManager] Live MCP discovery failed: ${e}`);
      const cached = this.context.globalState.get<DiscoverySnapshot>(
        STATE_KEY_MCP_DISCOVERY,
      );
      if (cached) {
        Logger.info("[McpDiscoveryManager] Using cached MCP discovery snapshot.");
        return cached;
      }

      return null;
    }
  }

  /**
   * Cria um arquivo .mcp_config.json na raiz do workspace.
   */
  private createLocalDiscoveryFile() {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) return;

    // Prefer physical folders that are NOT mirrors
    const physicalFolder = folders.find((f) => {
      if (f.uri.scheme !== "file") return false;
      const fsPath = f.uri.fsPath.toLowerCase();
      return !fsPath.includes(".gx_mirror") && !fsPath.endsWith("genexus-kb");
    }) || folders.find((f) => f.uri.scheme === "file");

    if (!physicalFolder) return;

    const rootPath = physicalFolder.uri.fsPath;
    const configPath = path.join(rootPath, ".mcp_config.json");
    const port = this.getEffectivePort();

    const config = {
      mcpServers: {
        genexus: {
          type: "http",
          url: `http://127.0.0.1:${port}/mcp`,
          name: "GeneXus MCP Server",
          version: "1.0.0",
          capabilities: ["tools", "resources", "prompts", "completion"],
        },
      },
    };

    try {
      fs.writeFileSync(configPath, JSON.stringify(config, null, 2));
      Logger.info(
        `[McpDiscoveryManager] Discovery file created at ${configPath}`,
      );
    } catch (e) {
      Logger.error(
        `[McpDiscoveryManager] Failed to create discovery file: ${e}`,
      );
    }
  }

  private deleteLocalDiscoveryFilesForDevelopment() {
    const folders = vscode.workspace.workspaceFolders;
    const roots = new Set<string>();

    if (folders) {
      for (const folder of folders) {
        if (folder.uri.scheme === "file") {
          roots.add(folder.uri.fsPath);
        }
      }
    }

    roots.add(this.context.extensionPath);

    for (const root of roots) {
      if (!fs.existsSync(root)) {
        continue;
      }

      this.deleteDiscoveryFilesRecursively(root);
    }
  }

  private deleteDiscoveryFilesRecursively(rootPath: string) {
    const pending = [rootPath];

    while (pending.length > 0) {
      const current = pending.pop();
      if (!current) {
        continue;
      }

      let entries: fs.Dirent[];
      try {
        entries = fs.readdirSync(current, { withFileTypes: true });
      } catch {
        continue;
      }

      for (const entry of entries) {
        const fullPath = path.join(current, entry.name);

        if (entry.isDirectory()) {
          pending.push(fullPath);
          continue;
        }

        if (!entry.isFile() || entry.name !== ".mcp_config.json") {
          continue;
        }

        try {
          fs.unlinkSync(fullPath);
          Logger.info(
            `[McpDiscoveryManager] Removed local discovery file during development: ${fullPath}`,
          );
        } catch (e) {
          Logger.warn(
            `[McpDiscoveryManager] Failed to remove local discovery file during development: ${fullPath} (${e})`,
          );
        }
      }
    }
  }

  /**
   * Registra tools MCP dinamicamente como VS Code Language Model Tools.
   */
  private registerCopilotTools(discovery: DiscoverySnapshot | null) {
    try {
      const anyVscode = vscode as any;
      if (!anyVscode.lm || !anyVscode.lm.registerTool) {
        return;
      }

      const currentExtension = vscode.extensions.getExtension("lennix1337.nexus-ide");
      const contributedTools =
        currentExtension?.packageJSON?.contributes?.languageModelTools;
      if (!Array.isArray(contributedTools) || contributedTools.length === 0) {
        Logger.info(
          "[McpDiscoveryManager] Skipping LM tool bridge because the extension does not contribute static languageModelTools.",
        );
        return;
      }

      for (const registration of this._toolRegistrations) {
        registration.dispose();
      }
      this._toolRegistrations = [];

      const tools = discovery?.tools ?? [];
      for (const tool of tools) {
        if (!tool?.name) {
          continue;
        }

        const disposable = anyVscode.lm.registerTool(tool.name, {
          invoke: async (options: any, _token: vscode.CancellationToken) => {
            const args = options?.parameters ?? {};
            Logger.info(
              `[McpDiscoveryManager] Tool invoked: ${tool.name} with args: ${JSON.stringify(args)}`,
            );

            const result = await this.provider.callMcpTool(tool.name, args);
            return {
              content: [
                {
                  type: "text",
                  text:
                    typeof result === "string"
                      ? result
                      : JSON.stringify(result, null, 2),
                },
              ],
            };
          },
        });

        this._toolRegistrations.push(disposable);
      }

      if (tools.length > 0) {
        Logger.info(
          `[McpDiscoveryManager] Registered ${tools.length} MCP tools for VS Code LM.`,
        );
      }
    } catch (e) {
      Logger.error(`[McpDiscoveryManager] Failed to register MCP tools: ${e}`);
    }
  }

  /**
   * Comandos para registro global.
   */
  private registerCommands() {
    this.context.subscriptions.push(
      vscode.commands.registerCommand(
        `${COMMAND_PREFIX}.registerMcpGlobally`,
        async () => {
          const choice = await vscode.window.showInformationMessage(
            "Deseja registrar o GeneXus MCP no Claude Desktop?",
            "Sim (Recomendado)",
            "Nao",
          );

          if (choice === "Sim (Recomendado)") {
            await this.updateClaudeConfig();
          }
        },
      ),
    );
  }

  private async updateClaudeConfig() {
    const isWin = os.platform() === "win32";
    if (!isWin) return;

    const claudePath = path.join(
      os.homedir(),
      "AppData",
      "Roaming",
      "Claude",
      "claude_desktop_config.json",
    );
    const port = this.getEffectivePort();

    try {
      let config: any = { mcpServers: {} };
      if (fs.existsSync(claudePath)) {
        config = JSON.parse(fs.readFileSync(claudePath, "utf8"));
      }

      config.mcpServers = config.mcpServers || {};
      config.mcpServers.genexus = {
        command: "npx",
        args: [
          "-y",
          "@modelcontextprotocol/server-http",
          `http://127.0.0.1:${port}/mcp`,
        ],
      };

      fs.writeFileSync(claudePath, JSON.stringify(config, null, 2));
      vscode.window.showInformationMessage(
        "GeneXus MCP registrado no Claude Desktop com sucesso!",
      );
    } catch (e) {
      vscode.window.showErrorMessage(
        `Falha ao atualizar config do Claude: ${e}`,
      );
    }
  }
}
