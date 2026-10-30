import type { components } from "@docproc/api-client";

export type DocumentEvent = components["schemas"]["DocumentEventDto"];
export type DocumentStatus = components["schemas"]["DocumentStatus"];

/** `document.stage_failed` → `stage_failed`; the i18n key under `timeline.eventType.*` and the badge text fallback. */
export function eventKind(eventType: string): string {
  const dot = eventType.indexOf(".");
  return dot >= 0 ? eventType.slice(dot + 1) : eventType;
}

/** Event kinds that mean something went wrong; the row is highlighted and the summary shows the error. */
export const failureKinds: readonly string[] = ["stage_failed"];

/** Event kinds a human caused (the actor is a user, not `pipeline`). */
export const humanKinds: readonly string[] = ["approved", "rejected", "corrected", "reprocessed"];

/** The pipeline stage whose output is each progress status (PipelineStages.ResultingStatus, inverted). */
const stageByStatus: Partial<Record<DocumentStatus, string>> = {
  Received: "Ingest",
  Normalized: "Normalize",
  Split: "Split",
  Classified: "Classify",
  Matched: "Match",
  Extracted: "Extract",
  Validated: "Validate",
};

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

/**
 * The stage an event belongs to: the pipeline writes `details.stage` on failures and publish events; a
 * `stage_completed` transition is named by the status it reached; reprocess carries `fromStage`; otherwise none.
 */
export function eventStage(event: Pick<DocumentEvent, "details" | "toStatus" | "eventType">): string | null {
  const details = asRecord(event.details);
  const explicit = details?.stage ?? details?.fromStage;
  if (typeof explicit === "string" && explicit.trim() !== "") return explicit;
  if (typeof explicit === "number") return String(explicit);
  if (event.toStatus && eventKind(event.eventType) === "stage_completed") return stageByStatus[event.toStatus] ?? null;
  return null;
}

export interface DetailRow {
  /** Dotted path inside the details JSON (`error`, `flag.code`, `tried[0]`). */
  path: string;
  /** The value as text: strings verbatim, numbers/booleans stringified, null as `null`, empty containers as `{}`/`[]`. */
  value: string;
  /** JSON type of the leaf, for monospace vs prose rendering. */
  kind: "string" | "number" | "boolean" | "null" | "empty";
}

/**
 * Flattens an event's `details` JSON into leaf rows, depth first, in key order, so a table shows every value the
 * pipeline recorded without the admin reading raw JSON. A non-object (a bare string, a number) becomes one row at
 * path `value`; null/undefined gives no rows.
 */
export function detailRows(details: unknown, maxRows = 200): DetailRow[] {
  const rows: DetailRow[] = [];
  const visit = (value: unknown, path: string) => {
    if (rows.length >= maxRows) return;
    if (value === null || value === undefined) {
      rows.push({ path, value: "null", kind: "null" });
      return;
    }
    if (Array.isArray(value)) {
      if (value.length === 0) {
        rows.push({ path, value: "[]", kind: "empty" });
        return;
      }
      value.forEach((item, index) => visit(item, `${path}[${index}]`));
      return;
    }
    const record = asRecord(value);
    if (record) {
      const keys = Object.keys(record);
      if (keys.length === 0) {
        rows.push({ path, value: "{}", kind: "empty" });
        return;
      }
      for (const key of keys) visit(record[key], path === "" ? key : `${path}.${key}`);
      return;
    }
    if (typeof value === "string") rows.push({ path, value, kind: "string" });
    else if (typeof value === "number") rows.push({ path, value: String(value), kind: "number" });
    else if (typeof value === "boolean") rows.push({ path, value: value ? "true" : "false", kind: "boolean" });
    else rows.push({ path, value: String(value), kind: "string" });
  };
  if (details === null || details === undefined) return rows;
  visit(details, "");
  // A bare scalar lands at path "" — name it.
  return rows.map((row) => (row.path === "" ? { ...row, path: "value" } : row));
}

/** The one line the timeline shows next to an event before the details are expanded. */
export function summarizeEvent(event: Pick<DocumentEvent, "details" | "eventType">): string | null {
  const details = asRecord(event.details);
  if (!details) return typeof event.details === "string" ? event.details : null;
  for (const key of ["error", "reason", "message", "code", "eventType"]) {
    const value = details[key];
    if (typeof value === "string" && value.trim() !== "") return value;
  }
  if (eventKind(event.eventType) === "corrected") {
    const paths = details.paths ?? details.corrections;
    if (Array.isArray(paths)) return paths.map((p) => (typeof p === "string" ? p : asRecord(p)?.path)).filter(Boolean).join(", ") || null;
  }
  return null;
}

/** Pretty JSON for the raw view under the rows; never throws on cyclic or exotic input. */
export function detailsJson(details: unknown): string {
  try {
    return JSON.stringify(details, null, 2) ?? "null";
  } catch {
    return String(details);
  }
}
