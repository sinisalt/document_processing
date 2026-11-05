/**
 * The tenant's typed attribute schema (`settings.supplierAttributeSchema`, docs/11 §6.4): a JSON Schema object of the
 * subset the API accepts, `properties` of type string, number, integer or boolean with `title`, `description`, `enum`,
 * `minLength`, `maxLength`, `pattern`, `minimum`, `maximum`, and `required`. The supplier, group and entity attribute
 * forms are generated from it.
 */
export type AttributeKind = "string" | "number" | "integer" | "boolean";

export interface AttributeField {
  key: string;
  kind: AttributeKind;
  title: string;
  description?: string;
  options?: string[];
  required: boolean;
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  minimum?: number;
  maximum?: number;
}

export type AttributeValue = string | number | boolean;
export type AttributeValues = Record<string, AttributeValue | null>;

const KINDS = new Set<AttributeKind>(["string", "number", "integer", "boolean"]);

/** The schema's fields in the order the schema lists them; a schema of another shape (or none) has none. */
export function attributeFields(schema: unknown): AttributeField[] {
  if (!schema || typeof schema !== "object" || Array.isArray(schema)) return [];
  const { properties, required } = schema as { properties?: Record<string, Record<string, unknown>>; required?: unknown };
  if (!properties || typeof properties !== "object") return [];
  const requiredKeys = new Set(Array.isArray(required) ? required.filter((k): k is string => typeof k === "string") : []);
  return Object.entries(properties)
    .filter(([, p]) => p && typeof p === "object" && KINDS.has(p.type as AttributeKind))
    .map(([key, p]) => ({
      key,
      kind: p.type as AttributeKind,
      title: typeof p.title === "string" && p.title ? p.title : key,
      description: typeof p.description === "string" ? p.description : undefined,
      options: Array.isArray(p.enum) ? p.enum.map(String) : undefined,
      required: requiredKeys.has(key),
      minLength: typeof p.minLength === "number" ? p.minLength : undefined,
      maxLength: typeof p.maxLength === "number" ? p.maxLength : undefined,
      pattern: typeof p.pattern === "string" ? p.pattern : undefined,
      minimum: typeof p.minimum === "number" ? p.minimum : undefined,
      maximum: typeof p.maximum === "number" ? p.maximum : undefined,
    }));
}

/** The form's text for a stored value ("" for none). */
export function attributeText(value: unknown): string {
  if (value === null || value === undefined) return "";
  return String(value);
}

/** A typed value from the form's text: "" clears it; a number field takes a number; null when the text does not read as one. */
export function parseAttribute(field: AttributeField, text: string | boolean): { value: AttributeValue | null } | { error: "number" | "integer" } {
  if (field.kind === "boolean") return { value: text === true || text === "true" };
  const trimmed = String(text).trim();
  if (trimmed === "") return { value: null };
  if (field.kind === "number" || field.kind === "integer") {
    const number = Number(trimmed.replace(",", "."));
    if (!Number.isFinite(number)) return { error: "number" };
    if (field.kind === "integer" && !Number.isInteger(number)) return { error: "integer" };
    return { value: number };
  }
  return { value: trimmed };
}

/**
 * The attributes to store: the schema's fields from the form (an empty one left out), merged over the stored
 * attributes the schema does not name, so a key the schema does not know is kept.
 */
export function mergeAttributes(stored: Record<string, unknown> | null | undefined, fields: AttributeField[], values: AttributeValues): Record<string, unknown> {
  const known = new Set(fields.map((f) => f.key));
  const next: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(stored ?? {})) if (!known.has(key)) next[key] = value;
  for (const field of fields) {
    const value = values[field.key];
    if (value !== null && value !== undefined && !(field.kind === "boolean" && value === false && stored?.[field.key] === undefined)) next[field.key] = value;
  }
  return next;
}
