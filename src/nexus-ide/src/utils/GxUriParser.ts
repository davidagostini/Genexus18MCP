import * as vscode from "vscode";
import * as path from "path";
import * as fs from "fs";
import { GX_SCHEME } from "../constants";
import { GxPartMapper } from "./GxPartMapper";
import { Logger } from "./Logger";

export interface GxUriInfo {
  type: string;
  name: string;
  part: string;
  path: string;
  parentPath?: string;
  containerPath?: string;
}

type MirrorIndexEntry = GxUriInfo & {
  path: string;
};

export class GxUriParser {
  private static _shadowRoot?: string;
  private static _mirrorIndexByPath = new Map<string, MirrorIndexEntry>();
  private static _mirrorIndexByObject = new Map<string, string>();

  private static getObjectKey(type: string, name: string, part: string): string {
    return `${type}:${name}:${part}`.toLowerCase();
  }

  private static normalizeRelativePath(targetPath: string): string {
    return targetPath.replace(/\\/g, "/").replace(/^\/+/, "");
  }

  static getMirrorIndexPath(shadowRoot?: string): string | null {
    const base = shadowRoot || this._shadowRoot;
    if (!base) return null;
    return path.join(base, ".gx_index.json");
  }

  static configureShadowRoot(shadowRoot: string | undefined) {
    this._shadowRoot = shadowRoot
      ? shadowRoot.replace(/[\\/]+$/, "")
      : undefined;
  }

  static clearMirrorIndex() {
    this._mirrorIndexByPath.clear();
    this._mirrorIndexByObject.clear();
  }

  static loadMirrorIndex(shadowRoot?: string) {
    const indexPath = this.getMirrorIndexPath(shadowRoot);
    this.clearMirrorIndex();

    if (!indexPath || !fs.existsSync(indexPath)) {
      return;
    }

    try {
      const raw = fs.readFileSync(indexPath, "utf8");
      const entries = JSON.parse(raw);
      if (!Array.isArray(entries)) {
        return;
      }

      for (const entry of entries) {
        if (
          !entry ||
          typeof entry.path !== "string" ||
          typeof entry.type !== "string" ||
          typeof entry.name !== "string" ||
          typeof entry.part !== "string"
        ) {
          continue;
        }

        this.registerMirrorEntry(entry);
      }
    } catch (e) {
      Logger.warn(`[Nexus IDE] Mirror index at ${indexPath} is unreadable or corrupt: ${e}`);
      this.clearMirrorIndex();
    }
  }

  static registerMirrorEntry(entry: MirrorIndexEntry) {
    const normalizedPath = this.normalizeRelativePath(entry.path);
    const normalizedEntry: MirrorIndexEntry = {
      ...entry,
      path: normalizedPath,
    };

    this._mirrorIndexByPath.set(normalizedPath.toLowerCase(), normalizedEntry);
    this._mirrorIndexByObject.set(
      this.getObjectKey(entry.type, entry.name, entry.part),
      normalizedPath,
    );
  }

  static findMirrorPath(type: string, name: string, part = "Source"): string | null {
    const relativePath = this._mirrorIndexByObject.get(
      this.getObjectKey(type, name, part),
    );
    if (!relativePath || !this._shadowRoot) {
      return null;
    }

    return path.join(this._shadowRoot, relativePath);
  }

  static isGeneXusUri(uri: vscode.Uri | undefined): boolean {
    if (!uri) return false;
    if (uri.scheme === GX_SCHEME) return true;
    if (uri.scheme !== "file" || !this._shadowRoot) return false;

    return uri.fsPath.toLowerCase().startsWith(this._shadowRoot.toLowerCase());
  }

  private static parseFileUri(uri: vscode.Uri): GxUriInfo | null {
    if (uri.scheme !== "file" || !this._shadowRoot) return null;

    const normalizedFsPath = uri.fsPath.replace(/[\\/]+$/, "");
    if (!normalizedFsPath.toLowerCase().startsWith(this._shadowRoot.toLowerCase())) return null;

    const relativePath = this.normalizeRelativePath(
      uri.fsPath
      .substring(this._shadowRoot.length)
    );

    const indexed = this._mirrorIndexByPath.get(relativePath.toLowerCase());
    if (indexed) {
      return indexed;
    }

    const segments = relativePath.split("/").filter((segment) => segment.length > 0);
    const fileName = segments.pop() || "";
    if (!fileName.endsWith(".gx")) return null;

    let cleanName = fileName.replace(/\.gx$/i, "");
    let part = "Source";

    const nameParts = cleanName.split(".");
    if (nameParts.length > 1) {
      part = nameParts.pop()!;
      cleanName = nameParts.join(".");
    }
    cleanName = GxPartMapper.stripTypeSuffix(cleanName);

    const parentSegment = segments.length > 0 ? segments[segments.length - 1] : "";
    return {
      type: parentSegment,
      name: cleanName,
      part,
      path: relativePath,
    };
  }

  /**
   * Parses a GeneXus URI into its components.
   * Format: gxkb18:/Type/Name.gx#Part or gxkb18:/Type/Name.Part.gx
   */
  static parse(uri: vscode.Uri): GxUriInfo | null {
    if (uri.scheme === "file") {
      return this.parseFileUri(uri);
    }

    if (uri.scheme !== GX_SCHEME) return null;

    const pathStr = decodeURIComponent(uri.path.substring(1));
    const parts = pathStr.split("/");
    
    // Example: /Procedure/MyProc.Source.gx
    const fileName = parts.pop() || "";
    const type = parts.pop() || "";
    
    // Remove .gx suffix
    let cleanName = fileName.replace(".gx", "");
    let part = "Source"; // Default

    // Handle part in name (e.g., MyProc.Source.gx)
    const nameParts = cleanName.split(".");
    if (nameParts.length > 1) {
      part = nameParts.pop()!;
      cleanName = nameParts.join(".");
    }
    cleanName = GxPartMapper.stripTypeSuffix(cleanName);

    return {
      type,
      name: cleanName,
      part,
      path: pathStr
    };
  }

  /**
   * Resolves the active GeneXus editor URI, with fallback to visible editors.
   */
  static getActiveGxUri(): vscode.Uri | null {
    const activeEditor = vscode.window.activeTextEditor;
    if (activeEditor && this.isGeneXusUri(activeEditor.document.uri)) {
      return activeEditor.document.uri;
    }

    // Fallback to the first visible GeneXus editor
    const visibleGxEditor = vscode.window.visibleTextEditors.find(
      (e) => this.isGeneXusUri(e.document.uri)
    );
    
    return visibleGxEditor?.document.uri || null;
  }

  /**
   * Gets the object name from a GeneXus URI.
   */
  static getObjectName(uri: vscode.Uri): string {
    const info = this.parse(uri);
    return info?.name || "";
  }

  static toEditorUri(type: string, name: string, part = "Source"): vscode.Uri {
    const mirrorPath = this.findMirrorPath(type, name, part);
    if (mirrorPath && fs.existsSync(mirrorPath)) {
      return vscode.Uri.file(mirrorPath);
    }

    const partSuffix = part === "Source" ? "" : `.${part}`;
    return vscode.Uri.parse(`${GX_SCHEME}:/${type}/${name}${partSuffix}.gx`);
  }
}
