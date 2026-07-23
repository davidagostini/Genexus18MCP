import * as vscode from "vscode";

/**
 * VS Code's default word pattern excludes '&', so a GeneXus variable's word range never
 * includes its leading '&' even when the cursor sits inside "&Total" — `range` covers just
 * "Total". Detect the variable prefix by peeking at the character immediately before the
 * matched word range. Shared by renameProvider.ts and referenceProvider.ts so the two
 * can't drift on what counts as "the cursor is on a variable".
 */
export function isVariableToken(
  document: vscode.TextDocument,
  range: vscode.Range,
  word: string,
): boolean {
  if (word.startsWith("&")) return true;
  if (range.start.character === 0) return false;

  const precedingChar = document.getText(
    new vscode.Range(range.start.translate(0, -1), range.start),
  );
  return precedingChar === "&";
}

/** Strips a leading '&' from a variable token, if present. */
export function stripVariablePrefix(word: string): string {
  return word.startsWith("&") ? word.substring(1) : word;
}
