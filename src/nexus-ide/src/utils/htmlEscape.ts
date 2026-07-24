/**
 * Escapes a value for safe interpolation into HTML text or a double/single-quoted
 * attribute. Escaping all five of & < > " ' prevents both tag injection and
 * breaking out of an attribute to add an event-handler attribute. Non-string
 * inputs are coerced via String() (KB payloads are not guaranteed typed).
 */
export function escapeHtml(value: unknown): string {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
