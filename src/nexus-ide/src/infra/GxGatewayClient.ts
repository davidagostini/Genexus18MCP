import * as http from "http";
import * as vscode from "vscode";
import { GxShadowService } from "../gxShadowService";
import { DEFAULT_MCP_PORT } from "../constants";
import { Logger } from "../utils/Logger";

const MCP_PROTOCOL_VERSION = "2025-11-25";
const SLOW_REQUEST_MS = 1200;

export class GxGatewayClient {
  private _baseUrl = `http://127.0.0.1:${DEFAULT_MCP_PORT}/mcp`;
  private _mcpSessionId?: string;
  private _shadowService?: GxShadowService;
  private static readonly statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10);
  private static activeRequests = 0;

  constructor(baseUrl: string, shadowService?: GxShadowService) {
    this._baseUrl = baseUrl;
    this._shadowService = shadowService;
  }

  public get baseUrl(): string {
    return this._baseUrl;
  }

  set baseUrl(url: string) {
    this._baseUrl = url;
  }

  private get mcpBaseUrl(): string {
    if (this._baseUrl.endsWith("/mcp")) {
      return this._baseUrl;
    }
    return this._baseUrl;
  }

  async initializeMcpSession(customTimeout?: number, signal?: AbortSignal): Promise<string> {
    if (this._mcpSessionId) {
      return this._mcpSessionId;
    }

    const response = await this.postRawJsonRpc(
      this.mcpBaseUrl,
      {
        jsonrpc: "2.0",
        id: "initialize",
        method: "initialize",
        params: {
          protocolVersion: MCP_PROTOCOL_VERSION,
          capabilities: {},
          clientInfo: {
            name: "nexus-ide",
            version: "1.0.0",
          },
        },
      },
      customTimeout,
      {
        "MCP-Protocol-Version": MCP_PROTOCOL_VERSION,
      },
      signal,
    );

    const sessionId = response.headers["mcp-session-id"];
    if (!sessionId) {
      throw new Error("MCP session was not established by the gateway.");
    }

    this._mcpSessionId = Array.isArray(sessionId) ? sessionId[0] : sessionId;
    return this._mcpSessionId;
  }

  async callMcp(method: string, params?: any, customTimeout?: number, signal?: AbortSignal): Promise<any> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= 3; attempt++) {
      try {
        const sessionId = await this.initializeMcpSession(customTimeout, signal);
        const response = await this.postRawJsonRpc(
          this.mcpBaseUrl,
          {
            jsonrpc: "2.0",
            id: `${method}-${Date.now()}`,
            method,
            params,
          },
          customTimeout,
          {
            "MCP-Protocol-Version": MCP_PROTOCOL_VERSION,
            "MCP-Session-Id": sessionId,
          },
          signal,
        );

        const unwrapped = this.unwrapGatewayResponse(response.body);
        if (this.isExpiredSessionResponse(unwrapped) && attempt < 3) {
          this.resetMcpSession();
          continue;
        }

        return unwrapped;
      } catch (error) {
        lastError = error;
        if (this.isAbortError(error) || !this.isRetriableTransportError(error) || attempt === 3) {
          throw error;
        }

        this.resetMcpSession();
        await this.delay(350 * attempt);
      }
    }

    throw lastError instanceof Error
      ? lastError
      : new Error(String(lastError ?? "Unknown MCP error"));
  }

  async listMcpTools(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("tools/list", undefined, customTimeout);
    return Array.isArray(result?.tools) ? result.tools : [];
  }

  async listMcpResources(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("resources/list", undefined, customTimeout);
    return Array.isArray(result?.resources) ? result.resources : [];
  }

  async listMcpResourceTemplates(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp(
      "resources/templates/list",
      undefined,
      customTimeout,
    );
    return Array.isArray(result?.resourceTemplates)
      ? result.resourceTemplates
      : [];
  }

  async listMcpPrompts(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("prompts/list", undefined, customTimeout);
    return Array.isArray(result?.prompts) ? result.prompts : [];
  }

  async callMcpTool(name: string, args?: any, customTimeout?: number, signal?: AbortSignal): Promise<any> {
    return this.callMcp(
      "tools/call",
      {
        name,
        arguments: args ?? {},
      },
      customTimeout,
      signal,
    );
  }

  async readMcpResource(uri: string, customTimeout?: number): Promise<any> {
    return this.callMcp(
      "resources/read",
      {
        uri,
      },
      customTimeout,
    );
  }

  async getMcpPrompt(
    name: string,
    args?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcp(
      "prompts/get",
      {
        name,
        arguments: args ?? {},
      },
      customTimeout,
    );
  }

  private unwrapGatewayResponse(body: string): any {
    try {
      Logger.info(`[GxGateway] Response body received (length: ${body.length})`);
      const fullResponse = JSON.parse(body);

      if (fullResponse && fullResponse.result) {
        const mcpResult = fullResponse.result;
        const blocks = Array.isArray(mcpResult.content)
          ? mcpResult.content
          : Array.isArray(mcpResult.contents)
            ? mcpResult.contents
            : null;
        if (blocks && blocks.length > 0) {
          const text = blocks[0].text;
          try {
            const trimmed = text.trim();
            if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
              try {
                return JSON.parse(trimmed);
              } catch (innerE) {
                Logger.error(`[GxGateway] JSON parse error in content: ${innerE}`);
                return text;
              }
            }
            return text;
          } catch (e) {
            Logger.debug(`[GxGateway] Content text inspection failed: ${e}`);
            return text;
          }
        }

        Logger.info(`[GxGateway] Found result wrapper but no content list.`);
        return fullResponse.result;
      }

      Logger.info(`[GxGateway] No result wrapper found.`);
      return fullResponse;
    } catch (e) {
      Logger.warn(`[GxGateway] Gateway response body was not valid JSON; returning raw body: ${e}`);
      return body;
    }
  }

  private resetMcpSession(): void {
    this._mcpSessionId = undefined;
  }

  private isExpiredSessionResponse(payload: unknown): boolean {
    if (!payload || typeof payload !== "object") {
      return false;
    }

    const errorValue = (payload as { error?: unknown }).error;
    return typeof errorValue === "string" &&
      errorValue.toLowerCase().includes("unknown or expired mcp session");
  }

  private isAbortError(error: unknown): boolean {
    return (error instanceof Error && error.name === "AbortError") ||
      (typeof error === "object" && error !== null && (error as { code?: unknown }).code === "ABORT_ERR");
  }

  private isRetriableTransportError(error: unknown): boolean {
    const message = error instanceof Error ? error.message : String(error ?? "");
    const lowered = message.toLowerCase();
    return lowered.includes("econnreset") ||
      lowered.includes("socket hang up") ||
      lowered.includes("unknown or expired mcp session") ||
      lowered.includes("mcp session was not established") ||
      lowered.includes("connect econnrefused");
  }

  private async delay(ms: number): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, ms));
  }

  private async postRawJsonRpc(
    targetUrl: string,
    command: any,
    customTimeout?: number,
    extraHeaders?: Record<string, string>,
    signal?: AbortSignal,
  ): Promise<{ body: string; headers: http.IncomingHttpHeaders }> {
    return new Promise((resolve, reject) => {
      const requestLabel = this.describeCommand(command);
      const startedAt = Date.now();
      let finished = false;
      let req: http.ClientRequest | undefined;
      let removeAbortListener = (): void => undefined;

      const cleanupAbortListener = (): void => {
        removeAbortListener();
        removeAbortListener = (): void => undefined;
      };

      const failRequest = (error: Error, outcome: string): void => {
        if (finished) return;
        finished = true;
        cleanupAbortListener();
        this.finishTrackedRequest(requestLabel, startedAt, outcome);
        reject(error);
      };

      const onAbort = (): void => {
        const error = new Error("MCP request aborted.");
        error.name = "AbortError";
        (error as { code?: string }).code = "ABORT_ERR";
        failRequest(error, "aborted");
        req?.destroy();
      };

      try {
        GxGatewayClient.activeRequests++;
        this.updateBusyStatus(requestLabel);
        Logger.debug(`[GxGateway] -> ${requestLabel}`);

        if (this._shadowService && command.params) {
          command.params.shadowPath = this._shadowService.shadowRoot;
        }

        const data = JSON.stringify(command);
        const timeout = customTimeout || 120000;

        Logger.info(
          `[GxGateway] Calling: ${targetUrl} with module ${command.module ?? command.method}...`,
        );
        const url = new URL(targetUrl);
        req = http.request(
          url,
          {
            method: "POST",
            headers: {
              "Content-Type": "application/json",
              "Content-Length": Buffer.byteLength(data),
              ...(extraHeaders ?? {}),
            },
            timeout: timeout,
            signal,
          },
          (res) => {
            Logger.info(
              `[GxGateway] Response status: ${res.statusCode} for module: ${command.module ?? command.method}`,
            );
            let body = "";
            res.on("data", (chunk) => (body += chunk));
            res.on("end", () => {
              if (!finished) {
                finished = true;
                cleanupAbortListener();
                this.finishTrackedRequest(requestLabel, startedAt, `HTTP ${res.statusCode}`);
              }
              resolve({ body, headers: res.headers });
            });
          },
        );

        if (signal) {
          signal.addEventListener("abort", onAbort, { once: true });
          removeAbortListener = () => signal.removeEventListener("abort", onAbort);
          if (signal.aborted) {
            onAbort();
            return;
          }
        }

        req.on("timeout", () => {
          const error = new Error(`Timeout Gateway (${timeout / 1000}s)`);
          failRequest(error, "timeout");
          req?.destroy();
        });

        req.on("error", (error) => {
          failRequest(error, `error: ${error.message}`);
        });
        req.write(data);
        req.end();
      } catch (syncError) {
        if (!finished) {
          finished = true;
          cleanupAbortListener();
          // In case activeRequests was already incremented
          if (GxGatewayClient.activeRequests > 0) {
              this.finishTrackedRequest(requestLabel, startedAt, `sync_error: ${syncError}`);
          }
        }
        reject(syncError);
      }
    });
  }

  private describeCommand(command: any): string {
    if (command?.method === "tools/call") {
      return `tool:${command?.params?.name ?? "unknown"}`;
    }

    if (command?.method === "resources/read") {
      return `resource:${command?.params?.uri ?? "unknown"}`;
    }

    if (command?.method === "prompts/get") {
      return `prompt:${command?.params?.name ?? "unknown"}`;
    }

    return command?.method ?? "unknown";
  }

  private updateBusyStatus(requestLabel?: string): void {
    if (GxGatewayClient.activeRequests <= 0) {
      GxGatewayClient.statusBarItem.hide();
      return;
    }

    const suffix = requestLabel ? ` ${requestLabel}` : "";
    GxGatewayClient.statusBarItem.text = `$(sync~spin) GeneXus MCP: ${GxGatewayClient.activeRequests} op${GxGatewayClient.activeRequests === 1 ? "" : "s"}${suffix}`;
    GxGatewayClient.statusBarItem.tooltip = "Operacoes MCP em andamento";
    GxGatewayClient.statusBarItem.show();
  }

  private finishTrackedRequest(requestLabel: string, startedAt: number, outcome: string): void {
    const duration = Date.now() - startedAt;
    const slowMarker = duration >= SLOW_REQUEST_MS ? " SLOW" : "";
    Logger.debug(`[GxGateway] <- ${requestLabel} (${duration}ms) ${outcome}${slowMarker}`);
    GxGatewayClient.activeRequests = Math.max(0, GxGatewayClient.activeRequests - 1);
    this.updateBusyStatus();
  }
}
