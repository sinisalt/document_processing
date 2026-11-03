import type { components } from "@docproc/api-client";
import { getByPath } from "@docproc/app-shell";
import type { FieldSpec, Locales } from "./formSpec";
import { toEditable } from "./values";

/**
 * Map mode (docs/11 §12.3): the reviewer focuses a field, clicks or shift-clicks words or drags a box on the page, the
 * API reads the selection (`GET /documents/{id}/pages/{n}/words?bbox=`, or `regions:read` with OCR), and the reading of
 * the field's kind goes into the form with the place it was read from. These are the browser's pure parts: boxes in page
 * units (0 to 1000 on both axes, like every stored box), the hit test, the reading for a field and its `source`.
 */
export type PageWords = components["schemas"]["PageWordsResponse"];
export type PageWord = components["schemas"]["PageWordDto"];
export type ValueCandidate = components["schemas"]["ValueCandidateDto"];
export type CorrectionSource = components["schemas"]["CorrectionSource"];

export interface Box {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

/** The page's coordinate space as stored boxes use it. */
export const PAGE_UNITS = 1000;

/** A drag shorter than this (page units, on both axes) is a click. */
export const CLICK_TOLERANCE = 4;

const num = (value: number | string): number => (typeof value === "number" ? value : Number(value));

export function toBox(box: { x0: number | string; y0: number | string; x1: number | string; y1: number | string }): Box {
  return { x0: num(box.x0), y0: num(box.y0), x1: num(box.x1), y1: num(box.y1) };
}

/** Two corners of a drag in any order, clamped to the page, as a box. */
export function boxFromDrag(a: { x: number; y: number }, b: { x: number; y: number }): Box {
  const clamp = (v: number) => Math.min(PAGE_UNITS, Math.max(0, v));
  return { x0: clamp(Math.min(a.x, b.x)), y0: clamp(Math.min(a.y, b.y)), x1: clamp(Math.max(a.x, b.x)), y1: clamp(Math.max(a.y, b.y)) };
}

export function isClick(box: Box): boolean {
  return box.x1 - box.x0 < CLICK_TOLERANCE && box.y1 - box.y0 < CLICK_TOLERANCE;
}

export function union(boxes: readonly Box[]): Box | null {
  if (boxes.length === 0) return null;
  return boxes.reduce((u, b) => ({ x0: Math.min(u.x0, b.x0), y0: Math.min(u.y0, b.y0), x1: Math.max(u.x1, b.x1), y1: Math.max(u.y1, b.y1) }));
}

/** A box grown by `margin` on every side, so a query by box finds the words whose centre is just inside. */
export function grow(box: Box, margin = 1): Box {
  return boxFromDrag({ x: box.x0 - margin, y: box.y0 - margin }, { x: box.x1 + margin, y: box.y1 + margin });
}

/** The query parameter `bbox=x0,y0,x1,y1`, rounded to two decimals as the API stores boxes. */
export function bboxParam(box: Box): string {
  const r = (v: number) => String(Math.round(v * 100) / 100);
  return [box.x0, box.y0, box.x1, box.y1].map(r).join(",");
}

/** The index of the word under a point (page units), or -1: the smallest box containing it, so a word wins over a line. */
export function wordAt(words: readonly { box: Box }[], point: { x: number; y: number }, slack = 1.5): number {
  let best = -1;
  let bestArea = Number.POSITIVE_INFINITY;
  words.forEach((word, index) => {
    const b = word.box;
    if (point.x >= b.x0 - slack && point.x <= b.x1 + slack && point.y >= b.y0 - slack && point.y <= b.y1 + slack) {
      const area = (b.x1 - b.x0) * (b.y1 - b.y0);
      if (area < bestArea) {
        best = index;
        bestArea = area;
      }
    }
  });
  return best;
}

/**
 * The candidate kinds (`PrintedValues.CandidateKinds` on the server) that fill a field of a definition kind, best first.
 * A bare number is the decimal reading, which amounts and percentages take as well.
 */
export function candidateKindsFor(definitionKind: string | undefined, editor: FieldSpec["kind"]): string[] {
  switch (definitionKind ?? editor) {
    case "date":
      return ["date"];
    case "money":
      return ["money", "decimal", "integer"];
    case "decimal":
    case "number":
      return ["decimal", "integer"];
    case "integer":
      return ["integer", "decimal"];
    case "percent":
      return ["percent", "decimal"];
    case "currency":
      return ["currency", "text"];
    case "identifier":
      return ["identifier", "text"];
    default:
      return ["text", "identifier"];
  }
}

/** The reading that fits the field, or null when none of the readings is of a kind the field takes. */
export function pickCandidate(spec: Pick<FieldSpec, "kind" | "definitionKind">, candidates: readonly ValueCandidate[]): ValueCandidate | null {
  for (const kind of candidateKindsFor(spec.definitionKind, spec.kind)) {
    const found = candidates.find((c) => c.kind === kind);
    if (found) return found;
  }
  return null;
}

/**
 * What the form shows for a reading in the field: the editable text of its value in the UI language (an amount's
 * number, a date's ISO form, an identifier's text), and for a code list the code whose code or label the text names.
 */
export function editableFromCandidate(spec: Pick<FieldSpec, "kind" | "options" | "optionLabels">, candidate: ValueCandidate, locale = "en"): string {
  const value = candidate.value as unknown;
  if (spec.kind === "enum") {
    const text = typeof value === "string" ? value.trim() : String(value ?? "");
    return matchOption(spec, text) ?? text;
  }
  if (spec.kind === "money" && typeof value === "number") return toEditable("money", value, locale);
  if (spec.kind === "string" || spec.kind === "text" || spec.kind === "identifier") {
    if (value !== null && typeof value === "object" && "amount" in (value as object)) return String((value as { amount: unknown }).amount);
    return typeof value === "string" ? value : value === null || value === undefined ? "" : String(value);
  }
  return toEditable(spec.kind, value, locale);
}

function matchOption(spec: Pick<FieldSpec, "options" | "optionLabels">, text: string): string | null {
  const folded = text.toLocaleLowerCase();
  for (const option of spec.options ?? []) {
    if (option.toLocaleLowerCase() === folded) return option;
    const labels: Locales | undefined = spec.optionLabels?.[option];
    if (labels && Object.values(labels).some((label) => label.toLocaleLowerCase() === folded)) return option;
  }
  return null;
}

/** How the reviewer took the value (the `CorrectionSource.method` stored with it). */
export type MapMethod = "word" | "region" | "ocr";

/**
 * The `source` a mapped value is saved with (docs/11 §12.6): its page, box, the words of the stored text layer it was
 * read from (`[[page, index]]`, none for OCR words) and the text as printed.
 */
export function sourceOf(page: number, read: Pick<PageWords, "bBox" | "words" | "text">, method: MapMethod): CorrectionSource {
  const words = read.words.filter((w) => w.index !== null && w.index !== undefined).map((w) => [page, num(w.index as number | string)]);
  return {
    method: method === "word" ? "click" : method,
    page,
    bBox: read.bBox ?? null,
    words: words.length > 0 ? words : null,
    rawText: read.text ?? null,
  };
}

/**
 * The next field to map after `current` (Enter, docs/11 §12.3): the next required field in form order that is empty in
 * the form, wrapping around; repeating-group cells are left to the table tool. Null when every required field holds a value.
 */
export function nextEmptyRequired(specs: readonly FieldSpec[], values: unknown, current: string | null): FieldSpec | null {
  const candidates = specs.filter((s) => s.required && !s.readOnly && !s.path.includes("["));
  if (candidates.length === 0) return null;
  const start = current === null ? -1 : candidates.findIndex((s) => s.path === current);
  for (let step = 1; step <= candidates.length; step++) {
    const spec = candidates[(start + step + candidates.length) % candidates.length]!;
    if (spec.path === current) continue;
    const value = getByPath(values, spec.path);
    if (value === undefined || value === null || String(value).trim() === "") return spec;
  }
  return null;
}
