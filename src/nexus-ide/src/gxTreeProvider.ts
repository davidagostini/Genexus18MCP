import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";
import { GxUriParser } from "./utils/GxUriParser";
import { Logger } from "./utils/Logger";

const TYPE_ICON_FILE: Record<string, string> = {
  Module: "module",
  Folder: "folder",
  Procedure: "procedure",
  WebPanel: "webpanel",
  Transaction: "transaction",
  SDT: "sdt",
  StructuredDataType: "sdt",
  DataProvider: "dataprovider",
  DataView: "dataview",
  Attribute: "attribute",
  Table: "table",
  SDPanel: "sdpanel",
};

const CONTAINER_INDEX_FILE = ".gx_containers.json";
const ROOT_MODULE_GROUP = "root-modules";
const MAIN_PROGRAMS_GROUP = "main-programs";

type ContainerIndexEntry = {
  type: string;
  name: string;
  path: string;
  parentPath?: string;
};

type ContainerMetadata = {
  name: string;
  type: string;
  parentPath: string;
};

type GxTreeItemOptions = {
  label?: string;
  gxTypeOverride?: string;
  groupId?: string;
};

function getTypeFromResource(
  uri: vscode.Uri,
  gxTypeOverride?: string,
): string {
  if (gxTypeOverride) {
    return gxTypeOverride;
  }

  const info = GxUriParser.parse(uri);
  if (info?.type) {
    return info.type;
  }

  if (uri.scheme === "file" && fs.existsSync(uri.fsPath) && fs.statSync(uri.fsPath).isDirectory()) {
    return "Folder";
  }

  return "Object";
}

export class GxTreeItem extends vscode.TreeItem {
  public readonly groupId?: string;
  private readonly gxTypeOverride?: string;

  constructor(
    public readonly resourceUri: vscode.Uri,
    collapsibleState: vscode.TreeItemCollapsibleState,
    private readonly extensionUri: vscode.Uri,
    options?: GxTreeItemOptions,
  ) {
    super(resourceUri, collapsibleState);

    this.groupId = options?.groupId;
    this.gxTypeOverride = options?.gxTypeOverride;
    const gxType = getTypeFromResource(resourceUri, this.gxTypeOverride);
    const isContainer = collapsibleState !== vscode.TreeItemCollapsibleState.None;
    const label = options?.label ?? path.basename(resourceUri.fsPath || resourceUri.path);

    this.label = label;
    this.tooltip = `[${gxType}] ${label}`;
    this.contextValue = this.groupId ? this.groupId : `gx_${gxType.toLowerCase()}`;

    const iconFile = TYPE_ICON_FILE[gxType];
    if (iconFile) {
      const iconUri = vscode.Uri.joinPath(
        extensionUri,
        "resources",
        `${iconFile}.svg`,
      );
      this.iconPath = { light: iconUri, dark: iconUri };
    } else if (this.groupId === ROOT_MODULE_GROUP) {
      this.iconPath = new vscode.ThemeIcon("file-submodule");
    } else if (this.groupId === MAIN_PROGRAMS_GROUP) {
      this.iconPath = new vscode.ThemeIcon("list-tree");
    }

    if (!isContainer && !this.groupId) {
      this.command = {
        command: "vscode.open",
        title: "Open",
        arguments: [this.resourceUri],
      };
    }
  }

  get isVirtualGroup(): boolean {
    return !!this.groupId;
  }

  get gxName(): string {
    if (this.groupId) {
      return this.label?.toString() ?? "";
    }

    const parsed = GxUriParser.parse(this.resourceUri);
    return parsed?.name || path.basename(this.resourceUri.fsPath || this.resourceUri.path);
  }

  get gxType(): string {
    return getTypeFromResource(this.resourceUri, this.gxTypeOverride);
  }

  get gxParentPath(): string {
    return path.dirname(this.resourceUri.fsPath || this.resourceUri.path);
  }
}

export class GxTreeProvider implements vscode.TreeDataProvider<GxTreeItem> {
  private _onDidChangeTreeData = new vscode.EventEmitter<
    GxTreeItem | undefined | null | void
  >();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;
  private _cache = new Map<string, { items: GxTreeItem[]; time: number }>();

  constructor(
    private readonly shadowRoot: string,
    private readonly extensionUri: vscode.Uri,
  ) {}

  private getContainerIndexPath(): string {
    return path.join(this.shadowRoot, CONTAINER_INDEX_FILE);
  }

  private readContainerIndex(): ContainerIndexEntry[] {
    const indexPath = this.getContainerIndexPath();
    if (!fs.existsSync(indexPath)) {
      return [];
    }

    try {
      const raw = fs.readFileSync(indexPath, "utf8");
      const parsed = JSON.parse(raw);
      if (!Array.isArray(parsed)) {
        return [];
      }

      return parsed.filter((entry) =>
        entry &&
        typeof entry.path === "string" &&
        typeof entry.name === "string" &&
        typeof entry.type === "string",
      );
    } catch (e) {
      Logger.warn(`[Nexus IDE] Container index at ${indexPath} is unreadable or corrupt: ${e}`);
      return [];
    }
  }

  private buildFsItems(
    targetDir: string,
    filter?: (entry: fs.Dirent) => boolean,
  ): GxTreeItem[] {
    if (!fs.existsSync(targetDir) || !fs.statSync(targetDir).isDirectory()) {
      return [];
    }

    const containerMetadataByPath = new Map<string, ContainerMetadata>(
      this.readContainerIndex().map((entry) => [
        entry.path.toLowerCase(),
        {
          name: entry.name,
          type: entry.type,
          parentPath: entry.parentPath ?? "",
        },
      ]),
    );

    return fs
      .readdirSync(targetDir, { withFileTypes: true })
      .filter((entry) =>
        entry.name !== ".gx_index.json" &&
        entry.name !== ".gx_containers.json" &&
        entry.name !== ".mcp_config.json" &&
        (!filter || filter(entry))
      )
      .sort((a, b) => {
        if (a.isDirectory() !== b.isDirectory()) {
          return a.isDirectory() ? -1 : 1;
        }
        return a.name.localeCompare(b.name, undefined, { sensitivity: "base" });
      })
      .map((entry) => {
        const itemUri = vscode.Uri.file(path.join(targetDir, entry.name));
        const relativePath = path.relative(this.shadowRoot, itemUri.fsPath).replace(/\\/g, "/").toLowerCase();
        const containerMetadata = entry.isDirectory()
          ? containerMetadataByPath.get(relativePath)
          : undefined;
        return new GxTreeItem(
          itemUri,
          entry.isDirectory()
            ? vscode.TreeItemCollapsibleState.Collapsed
            : vscode.TreeItemCollapsibleState.None,
          this.extensionUri,
          {
            gxTypeOverride: containerMetadata?.type,
            label: containerMetadata?.name,
          },
        );
      });
  }

  private getRootGroupItems(): GxTreeItem[] {
    const rootEntries = this.buildFsItems(this.shadowRoot);
    if (rootEntries.length === 0) {
      return [];
    }

    const containerIndex = this.readContainerIndex();
    const rootModuleNames = new Set(
      containerIndex
        .filter((entry) =>
          entry.parentPath === "" &&
          entry.type.toLowerCase() === "module",
        )
        .map((entry) => entry.name.toLowerCase()),
    );

    if (rootModuleNames.size === 0) {
      return rootEntries;
    }

    const hasRootModules = rootEntries.some((entry) =>
      rootModuleNames.has(path.basename(entry.resourceUri.fsPath).toLowerCase()),
    );
    const hasMainPrograms = rootEntries.some((entry) =>
      !rootModuleNames.has(path.basename(entry.resourceUri.fsPath).toLowerCase()),
    );

    const items: GxTreeItem[] = [];
    if (hasMainPrograms) {
      items.push(
        new GxTreeItem(
          vscode.Uri.from({ scheme: "gxkb18-tree", path: "/main-programs" }),
          vscode.TreeItemCollapsibleState.Collapsed,
          this.extensionUri,
          { label: "Main Programs", gxTypeOverride: "Folder", groupId: MAIN_PROGRAMS_GROUP },
        ),
      );
    }

    if (hasRootModules) {
      items.push(
        new GxTreeItem(
          vscode.Uri.from({ scheme: "gxkb18-tree", path: "/root-module" }),
          vscode.TreeItemCollapsibleState.Collapsed,
          this.extensionUri,
          { label: "Root Module", gxTypeOverride: "Module", groupId: ROOT_MODULE_GROUP },
        ),
      );
    }

    return items.length > 0 ? items : rootEntries;
  }

  private getGroupedRootChildren(groupId: string): GxTreeItem[] {
    const rootEntries = this.buildFsItems(this.shadowRoot);
    const containerMetadataByPath = new Map<string, ContainerMetadata>(
      this.readContainerIndex()
        .filter((entry) => (entry.parentPath ?? "") === "")
        .map((entry) => [
          entry.path.toLowerCase(),
          {
            name: entry.name,
            type: entry.type,
            parentPath: entry.parentPath ?? "",
          },
        ]),
    );

    return rootEntries.filter((entry) => {
      const relativePath = path
        .relative(this.shadowRoot, entry.resourceUri.fsPath)
        .replace(/\\/g, "/")
        .toLowerCase();
      const metadata = containerMetadataByPath.get(relativePath);
      const isModule = metadata?.type.toLowerCase() === "module";
      return groupId === ROOT_MODULE_GROUP ? isModule : !isModule;
    });
  }

  refresh(): void {
    this._cache.clear();
    this._onDidChangeTreeData.fire();
  }

  refreshNode(item: GxTreeItem): void {
    this._cache.delete((item.resourceUri.fsPath || item.resourceUri.path).toLowerCase());
    this._onDidChangeTreeData.fire(item);
  }

  getTreeItem(element: GxTreeItem): vscode.TreeItem {
    return element;
  }

  async getChildren(element?: GxTreeItem): Promise<GxTreeItem[]> {
    const cacheKey = element
      ? `${element.groupId ?? "node"}:${element.resourceUri.fsPath || element.resourceUri.path}`.toLowerCase()
      : `root:${this.shadowRoot}`.toLowerCase();
    const cached = this._cache.get(cacheKey);
    if (cached && Date.now() - cached.time < 300000) {
      return cached.items;
    }

    let items: GxTreeItem[];
    if (!element) {
      items = this.getRootGroupItems();
    } else if (element.isVirtualGroup) {
      items = this.getGroupedRootChildren(element.groupId!);
    } else {
      items = this.buildFsItems(element.resourceUri.fsPath);
    }

    this._cache.set(cacheKey, { items, time: Date.now() });
    return items;
  }
}
