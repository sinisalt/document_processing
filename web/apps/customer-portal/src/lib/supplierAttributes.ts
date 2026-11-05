/**
 * A supplier's attributes (docs/11 §6.4: `{"erpVendorId": "V-10042"}`, what output mappings read as
 * `supplier.attributes`) as the supplier sheet edits them: one `key = value` per line. Until P4's attribute schema
 * gives them a form, a value is text, except that a line left as it was keeps the stored number, boolean or null.
 */
export type AttributeValue = string | number | boolean | null;
export type Attributes = Record<string, AttributeValue>;

const KEY = /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/;

export function attributesText(attributes: Attributes | null | undefined): string {
  return Object.entries(attributes ?? {})
    .map(([key, value]) => `${key} = ${value === null ? "" : String(value)}`)
    .join("\n");
}

/** The lines as attributes, or the first line that is not `key = value` with a valid key (1-based). */
export function parseAttributes(text: string, stored: Attributes | null | undefined): { attributes: Attributes } | { invalidLine: number } {
  const attributes: Attributes = {};
  const lines = text.split("\n");
  for (const [i, raw] of lines.entries()) {
    const line = raw.trim();
    if (!line) continue;
    const separator = line.search(/[=:]/);
    if (separator <= 0) return { invalidLine: i + 1 };
    const key = line.slice(0, separator).trim();
    const value = line.slice(separator + 1).trim();
    if (!KEY.test(key) || value.length > 400) return { invalidLine: i + 1 };
    const before = stored?.[key];
    attributes[key] = before !== undefined && (before === null ? value === "" : String(before) === value) ? before : value;
  }
  return Object.keys(attributes).length > 50 ? { invalidLine: 51 } : { attributes };
}
