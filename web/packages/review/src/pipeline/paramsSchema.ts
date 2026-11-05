/**
 * The parameter forms of step and rule kinds (docs/11 §10.2, §11, §19 "Parameter forms"): a kind's `paramsSchema` (from
 * `GET /step-kinds` and `GET /rule-kinds`) read into a flat list of controls. The schemas of this deployment are small
 * objects of strings, numbers, booleans, enums and string lists; anything else (an object of objects, `anyOf`) is
 * edited as JSON, so every parameter stays reachable. Pure, so the mapping is tested without rendering.
 */
export type JsonSchema = {
  type?: string | string[];
  enum?: unknown[];
  properties?: Record<string, JsonSchema>;
  required?: string[];
  items?: JsonSchema;
  minimum?: number;
  maximum?: number;
  maxLength?: number;
  maxItems?: number;
  format?: string;
  additionalProperties?: unknown;
  anyOf?: unknown[];
  [other: string]: unknown;
};

export type ParamControl = "text" | "expression" | "number" | "integer" | "boolean" | "enum" | "list" | "scalar" | "json";

export interface ParamField {
  name: string;
  control: ParamControl;
  required: boolean;
  options?: string[];
  min?: number;
  max?: number;
  maxLength?: number;
}

export type ParamValues = Record<string, unknown>;

function typeOf(schema: JsonSchema): string | string[] | undefined {
  return schema.type;
}

/** The control a parameter's schema asks for; `expressionParams` names the rule expressions (a monospace text area). */
export function controlOf(name: string, schema: JsonSchema, expressionParams: readonly string[] = []): ParamControl {
  if (expressionParams.includes(name)) return "expression";
  if (Array.isArray(schema.enum)) return "enum";
  const type = typeOf(schema);
  if (Array.isArray(type)) return type.every((t) => ["number", "string", "boolean", "integer"].includes(t)) ? "scalar" : "json";
  switch (type) {
    case "string":
      return (schema.maxLength ?? 0) > 400 ? "expression" : "text";
    case "number":
      return "number";
    case "integer":
      return "integer";
    case "boolean":
      return "boolean";
    case "array":
      return schema.items && (schema.items.type === "string" || schema.items.type === undefined) && !schema.items.properties ? "list" : "json";
    default:
      return "json";
  }
}

/** The fields of a kind's parameter object, required ones first; an empty list for a kind without parameters. */
export function paramFields(schema: JsonSchema | null | undefined, expressionParams: readonly string[] = []): ParamField[] {
  if (!schema || typeof schema !== "object" || !schema.properties) return [];
  const required = new Set(schema.required ?? []);
  const fields = Object.entries(schema.properties).map(([name, property]) => {
    const control = controlOf(name, property ?? {}, expressionParams);
    const field: ParamField = { name, control, required: required.has(name) };
    if (control === "enum") field.options = (property.enum ?? []).filter((v): v is string => typeof v === "string");
    if (typeof property.minimum === "number") field.min = property.minimum;
    if (typeof property.maximum === "number") field.max = property.maximum;
    if (typeof property.maxLength === "number") field.maxLength = property.maxLength;
    return field;
  });
  return [...fields.filter((f) => f.required), ...fields.filter((f) => !f.required)];
}

/** Whether a kind's schema takes no parameters at all (`additionalProperties: false` and no properties). */
export function takesNoParams(schema: JsonSchema | null | undefined): boolean {
  return !!schema && schema.additionalProperties === false && (!schema.properties || Object.keys(schema.properties).length === 0);
}

/** A value as the control shows it in its text input. */
export function displayValue(field: ParamField, value: unknown): string {
  if (value === undefined || value === null) return "";
  switch (field.control) {
    case "list":
      return Array.isArray(value) ? value.join(", ") : String(value);
    case "json":
      return JSON.stringify(value, null, 2);
    case "boolean":
      return value === true ? "true" : "false";
    default:
      return String(value);
  }
}

/** The outcome of reading a control's text: the value (undefined leaves the parameter out) or why it does not read. */
export type ReadResult = { value: unknown } | { error: "number" | "integer" | "json" | "range" };

/** Reads what the user typed into the value the parameter holds; an empty text leaves the parameter out. */
export function readValue(field: ParamField, text: string): ReadResult {
  const trimmed = text.trim();
  if (trimmed === "") return { value: undefined };
  switch (field.control) {
    case "number":
    case "integer": {
      const number = Number(trimmed.replace(",", "."));
      if (!Number.isFinite(number)) return { error: "number" };
      if (field.control === "integer" && !Number.isInteger(number)) return { error: "integer" };
      if ((field.min !== undefined && number < field.min) || (field.max !== undefined && number > field.max)) return { error: "range" };
      return { value: number };
    }
    case "boolean":
      return { value: trimmed === "true" };
    case "list":
      return {
        value: trimmed
          .split(/[,\n]/)
          .map((part) => part.trim())
          .filter(Boolean),
      };
    case "scalar": {
      if (trimmed === "true" || trimmed === "false") return { value: trimmed === "true" };
      const number = Number(trimmed.replace(",", "."));
      return { value: /^-?\d+([.,]\d+)?$/.test(trimmed) && Number.isFinite(number) ? number : trimmed };
    }
    case "json":
      try {
        return { value: JSON.parse(trimmed) as unknown };
      } catch {
        return { error: "json" };
      }
    default:
      return { value: field.control === "expression" ? text : trimmed };
  }
}

/** The parameters with one value set (or left out when undefined), as a new object. */
export function withParam(values: ParamValues, name: string, value: unknown): ParamValues {
  const next = { ...values };
  if (value === undefined) delete next[name];
  else next[name] = value;
  return next;
}

/** The required parameters that have no value yet. */
export function missingRequired(fields: readonly ParamField[], values: ParamValues): string[] {
  return fields.filter((f) => f.required && (values[f.name] === undefined || values[f.name] === "")).map((f) => f.name);
}
