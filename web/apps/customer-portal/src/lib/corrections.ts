import { getByPath } from "@docproc/app-shell";
import type { components } from "@docproc/api-client";
import type { FieldKind, FieldSpec } from "./canonical";

export type FieldCorrection = components["schemas"]["FieldCorrection"];

/** Parses what a reviewer typed into a number; accepts a decimal comma. Null when blank, NaN when malformed. */
export function parseAmount(text: string): number | null {
  const trimmed = text.trim();
  if (trimmed === "") return null;
  const lastComma = trimmed.lastIndexOf(",");
  const lastDot = trimmed.lastIndexOf(".");
  let normalized: string;
  if (lastComma >= 0 && lastDot >= 0) {
    // Both separators: the last one is the decimal mark, the other groups thousands (1.234,50 or 1,234.50).
    normalized = lastComma > lastDot ? trimmed.replace(/\./g, "").replace(",", ".") : trimmed.replace(/,/g, "");
  } else if (lastComma >= 0) {
    normalized = trimmed.replace(",", ".");
  } else {
    normalized = trimmed;
  }
  return Number(normalized.replace(/\s/g, ""));
}

/**
 * The JSON value `PATCH /documents/{id}/fields` expects for an edited text (docs/05): a bare string for an identifier,
 * a bare number for an amount (document currency), ISO dates as strings, enums as names, and `null` to clear.
 */
export function toCorrectionValue(kind: FieldKind, text: string): unknown {
  const trimmed = text.trim();
  if (trimmed === "") return null;
  switch (kind) {
    case "money":
    case "number": {
      const n = parseAmount(trimmed);
      return n === null || Number.isNaN(n) ? trimmed : n;
    }
    default:
      return trimmed;
  }
}

function normalize(text: unknown): string {
  return typeof text === "string" ? text.trim() : text === null || text === undefined ? "" : String(text);
}

/**
 * Whether two texts of a field mean the same value: amounts compare as numbers ("58,80" is the 58.8 on screen), the
 * rest as trimmed text. A correction to the same value is not one: the API would still mark the field Corrected by a
 * human with confidence 1 and move the extraction (and its page box) into the history.
 */
export function sameValue(kind: FieldKind, before: string, after: string): boolean {
  if (before === after) return true;
  if (kind === "money" || kind === "number") {
    const a = parseAmount(before);
    const b = parseAmount(after);
    return a !== null && b !== null && !Number.isNaN(a) && !Number.isNaN(b) && a === b;
  }
  return false;
}

/**
 * Only what the reviewer changed, as corrections: a spec whose current value differs from its initial value yields one
 * `{ path, value }`; untouched fields (and fields retyped to the same value) are never sent, so the API keeps their
 * provenance byte for byte.
 */
export function diffCorrections(specs: readonly FieldSpec[], initial: unknown, current: unknown, reason?: string | null): FieldCorrection[] {
  const corrections: FieldCorrection[] = [];
  for (const spec of specs) {
    const before = normalize(getByPath(initial, spec.path));
    const after = normalize(getByPath(current, spec.path));
    if (sameValue(spec.kind, before, after)) continue;
    corrections.push({ path: spec.path, value: toCorrectionValue(spec.kind, after), reason: reason?.trim() ? reason.trim() : null });
  }
  return corrections;
}
