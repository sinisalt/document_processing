import { getByPath, isFieldValue, joinPath, parsePath, setByPath, type FieldValue } from "@docproc/app-shell";
import type { FieldKind, FieldSpec } from "./formSpec";

export interface Money {
  amount: number;
  currency: string;
}

export interface DocumentIdentifier {
  verbatim: string;
  normalized?: string;
}

/** The `FieldValue` a spec belongs to, read from the canonical JSON. */
export function fieldOf(canonical: unknown, spec: Pick<FieldSpec, "fieldPath">): FieldValue | undefined {
  const value = getByPath(canonical, spec.fieldPath);
  return isFieldValue(value) ? value : undefined;
}

/**
 * The raw value a spec addresses: the field's `value`, or a property inside it for nested specs. A Missing field has
 * none: the API serialises a missing number as the type's default (`{ value: 0, status: "Missing" }`), which the form
 * would otherwise show as a discount of 0.
 */
export function rawValueOf(canonical: unknown, spec: Pick<FieldSpec, "path" | "fieldPath">): unknown {
  const field = fieldOf(canonical, spec);
  if (!field || field.status === "Missing") return undefined;
  if (spec.path === spec.fieldPath) return field.value;
  const rest = parsePath(spec.path).slice(parsePath(spec.fieldPath).length);
  return getByPath(field.value, rest);
}

/**
 * The text the form shows for a raw canonical value. Numbers are written in the UI language as the document prints
 * them: an amount with (at least) two decimals, "9,60" in German and "9.60" in English rather than JavaScript's "9.6";
 * a quantity or percentage with the decimals it has. Never grouped: "1.234" would read back as 1.234 in English.
 */
export function toEditable(kind: FieldKind, raw: unknown, locale = "en"): string {
  if (raw === null || raw === undefined) return "";
  switch (kind) {
    case "identifier":
      return typeof raw === "object" ? String((raw as DocumentIdentifier).verbatim ?? "") : String(raw);
    case "money":
      return numberToText(typeof raw === "object" ? (raw as Money).amount : raw, locale, 2);
    case "number":
      return numberToText(raw, locale, 0);
    case "date":
      return String(raw).slice(0, 10);
    case "boolean":
      return raw === true || raw === "true" ? "true" : raw === false || raw === "false" ? "false" : "";
    default:
      return typeof raw === "object" ? JSON.stringify(raw) : String(raw);
  }
}

function numberToText(value: unknown, locale: string, minimumFractionDigits: number): string {
  // The generated client types a double as `number | string`; a string that is not a number is shown as it is.
  const n = typeof value === "number" ? value : typeof value === "string" && value.trim() !== "" ? Number(value) : Number.NaN;
  if (!Number.isFinite(n)) return typeof value === "string" ? value : "";
  return new Intl.NumberFormat(locale, { useGrouping: false, minimumFractionDigits, maximumFractionDigits: 10 }).format(n);
}

/** react-hook-form addresses nested values with dots only: `lines[2].quantity` → `lines.2.quantity`. */
export function formName(path: string): string {
  return parsePath(path)
    .map((segment) => String(segment))
    .join(".");
}

/** The inverse of `formName`. */
export function pathOfFormName(name: string): string {
  return joinPath(name.split(".").map((segment) => (/^\d+$/.test(segment) ? Number(segment) : segment)));
}

/** DOM id of a field's input, so flags and overlay boxes can focus it. */
export function fieldDomId(path: string): string {
  return `field-${path.replace(/[^\w-]/g, "_")}`;
}

/** The form's initial values: a nested object with the editable text of every spec, numbers in the UI language. */
export function toFormValues(canonical: unknown, specs: readonly FieldSpec[], locale = "en"): Record<string, unknown> {
  let values: Record<string, unknown> = {};
  for (const spec of specs) {
    values = setByPath(values, spec.path, toEditable(spec.kind, rawValueOf(canonical, spec), locale));
  }
  return values;
}

/** The currency a money field carries, else the one its definition points at (`currencyPath`), else the document's. */
export function currencyOf(canonical: unknown, spec: Pick<FieldSpec, "path" | "fieldPath" | "currencyPath">): string | null {
  const raw = rawValueOf(canonical, spec);
  if (raw && typeof raw === "object" && typeof (raw as Money).currency === "string") return (raw as Money).currency;
  return currencyAt(canonical, spec.currencyPath);
}

/** The currency at a definition's `currencyPath` (a top-level field), else the document's `currency`. */
export function currencyAt(canonical: unknown, currencyPath?: string | null): string | null {
  for (const path of [currencyPath, "currency"]) {
    if (!path) continue;
    const field = getByPath(canonical, path);
    const value = isFieldValue(field) && field.status !== "Missing" ? field.value : undefined;
    if (typeof value === "string" && value) return value;
  }
  return null;
}
