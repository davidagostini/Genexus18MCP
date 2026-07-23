import * as fs from "fs";
import * as path from "path";
import * as os from "os";
import { createHash } from "crypto";
import * as vscode from "vscode";
import { CONFIG_MCP_PORT, DEFAULT_MCP_PORT } from "../constants";
import { Logger } from "./Logger";

export const GATEWAY_LEASE_STALE_AFTER_MS = 45_000;

export type GatewayIdentity = {
  instanceKey: string;
  leasePath: string;
  port: number;
  kbPath: string;
  installationPath: string;
  shadowPath: string;
};

export type GatewayLeaseRecord = {
  instanceKey: string;
  processId: number;
  httpPort: number;
  kbPath: string;
  programDir: string;
  shadowPath: string;
  updatedUtc: string;
};

export function readJsonFile(filePath: string): any {
  const raw = fs.readFileSync(filePath, "utf8");
  const normalized = raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw;
  return JSON.parse(normalized);
}

export function resolveGatewayConfigPath(extensionPath: string): string {
  const candidates = [
    path.resolve(extensionPath, "..", "..", "config.json"),
    path.join(extensionPath, "backend", "config.json"),
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }

  return candidates[0];
}

export function tryReadGatewayConfig(extensionPath: string): any | undefined {
  const configPath = resolveGatewayConfigPath(extensionPath);
  if (!fs.existsSync(configPath)) {
    return undefined;
  }

  try {
    return readJsonFile(configPath);
  } catch (e) {
    Logger.warn(`[Nexus IDE] Gateway config at ${configPath} is unreadable or corrupt: ${e}`);
    return undefined;
  }
}

export function resolveGatewayHttpPort(
  extensionPath: string,
  workspaceConfig?: vscode.WorkspaceConfiguration,
): number {
  if (workspaceConfig) {
    const configuredPort = workspaceConfig.inspect<number>(CONFIG_MCP_PORT);
    const hasExplicitPort =
      configuredPort?.workspaceValue !== undefined ||
      configuredPort?.workspaceFolderValue !== undefined ||
      configuredPort?.globalValue !== undefined;

    if (hasExplicitPort) {
      return workspaceConfig.get(CONFIG_MCP_PORT, DEFAULT_MCP_PORT);
    }
  }

  const config = tryReadGatewayConfig(extensionPath);
  const canonicalPort = config?.Server?.HttpPort;
  return Number.isInteger(canonicalPort) && canonicalPort > 0
    ? canonicalPort
    : DEFAULT_MCP_PORT;
}

export function buildGatewayIdentity(
  extensionPath: string,
  workspaceConfig: vscode.WorkspaceConfiguration | undefined,
  kbPath: string,
  installationPath: string,
): GatewayIdentity {
  const config = tryReadGatewayConfig(extensionPath);
  const port = resolveGatewayHttpPort(extensionPath, workspaceConfig);
  const normalizedKbPath = normalizeGatewayPath(kbPath);
  const normalizedInstallationPath = normalizeGatewayPath(installationPath);
  const normalizedShadowPath = normalizeGatewayPath(
    config?.Environment?.GX_SHADOW_PATH ||
      (normalizedKbPath ? path.join(normalizedKbPath, ".gx_mirror") : ""),
  );
  const instanceKey = `port=${port}|kb=${normalizedKbPath}|program=${normalizedInstallationPath}|shadow=${normalizedShadowPath}`;

  return {
    instanceKey,
    leasePath: resolveGatewayLeasePath(instanceKey),
    port,
    kbPath: normalizedKbPath,
    installationPath: normalizedInstallationPath,
    shadowPath: normalizedShadowPath,
  };
}

export function readGatewayLease(leasePath: string): GatewayLeaseRecord | undefined {
  if (!fs.existsSync(leasePath)) {
    return undefined;
  }

  try {
    return readJsonFile(leasePath) as GatewayLeaseRecord;
  } catch (e) {
    Logger.warn(`[Nexus IDE] Gateway lease at ${leasePath} is unreadable or corrupt: ${e}`);
    return undefined;
  }
}

function resolveGatewayLeasePath(instanceKey: string): string {
  const localAppData =
    process.env.LOCALAPPDATA ||
    path.join(os.homedir(), "AppData", "Local");
  const leaseDirectory = path.join(localAppData, "GenexusMCP", "gateway-leases");
  fs.mkdirSync(leaseDirectory, { recursive: true });
  return path.join(leaseDirectory, `${computeLeaseHash(instanceKey)}.json`);
}

function computeLeaseHash(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}

function normalizeGatewayPath(rawPath: string | undefined): string {
  if (!rawPath) {
    return "";
  }

  try {
    return path.resolve(rawPath).replace(/[\\/]+$/, "").toLowerCase();
  } catch (e) {
    Logger.debug(`[Nexus IDE] path.resolve failed for "${rawPath}"; using raw fallback: ${e}`);
    return rawPath.trim().replace(/[\\/]+$/, "").toLowerCase();
  }
}
