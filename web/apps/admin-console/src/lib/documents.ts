import type { components } from "@docproc/api-client";
import { fieldStatuses, formatDate, formatMoney, isFieldValue, type FieldValue } from "@docproc/app-shell";

export type DocumentStatus = components["schemas"]["DocumentStatus"];
export type DocumentType = components["schemas"]["DocumentType"];
export type FlagSeverity = components["schemas"]["FlagSeverity"];

/** Every document status, in state-machine order (docs/03). */
export const documentStatuses: readonly DocumentStatus[] = [
  "Received",
  "Normalized",
  "Split",
  "Classified",
  "Matched",
  "Extracted",
  "Validated",
  "AutoApproved",
  "NeedsReview",
  "Approved",
  "Rejected",
  "Failed",
];

export const documentTypes: readonly DocumentType[] = ["Unknown", "Invoice", "CreditNote", "PurchaseOrder", "DeliveryNote", "Statement", "Reminder", "Other"];

// The reprocess-stage rules are shared with the customer portal's Reprocess dialog.
export { canReprocessFrom, defaultReprocessStage, furthestPipelineStatus, pipelineOrder, reprocessStages, reprocessStartStatus, type ReprocessStage } from "@docproc/app-shell";

/** Statuses `:reprocess` accepts a document in (DocumentStateMachine.CanReprocess). */
export const reprocessableStatuses: readonly DocumentStatus[] = ["Failed", "NeedsReview", "Rejected", "Validated"];

/** Statuses `:approve` / `:reject` act on. */
export const decidableStatuses: readonly DocumentStatus[] = ["NeedsReview", "Validated"];

export function canReprocess(status: DocumentStatus): boolean {
  return reprocessableStatuses.includes(status);
}

export function canDecide(status: DocumentStatus): boolean {
  return decidableStatuses.includes(status);
}

/**
 * A short handle for an id the screens show when nothing better exists (a document without a number, a tenant
 * without a name): its last eight hex digits. The ids are UUIDv7, whose leading digits are the creation time, so the
 * first eight were the same for every document received in the same minute (two untitled rows both read `01a19012`).
 */
export function shortId(id: string): string {
  const hex = id.replace(/-/g, "");
  return hex.length > 8 ? hex.slice(-8) : hex;
}

/** What a document is called in labels, toasts and dialog titles: its number, else its short id. */
export function documentLabel(document: { id: string; documentNumber?: string | null }): string {
  return document.documentNumber?.trim() || shortId(document.id);
}

/** The detail page's subtitle: the parts that are there (tenant, type, supplier, id), joined by a middle dot. */
export function documentSubtitle(parts: readonly (string | null | undefined)[]): string {
  return parts
    .map((part) => part?.trim())
    .filter((part): part is string => !!part)
    .join(" · ");
}

/**
 * The count a status card can honestly show from one page: the page size when the page was full and more follow
 * (`200+`), else the exact number of items.
 */
export function pageCountLabel(itemCount: number, nextCursor: string | null | undefined, pageSize: number): string {
  if (nextCursor && itemCount >= pageSize) return `${itemCount}+`;
  return String(itemCount);
}

export interface CanonicalRow {
  /** Canonical field path (`invoiceNumber`, `supplier`, `lines`). */
  path: string;
  /** Formatted value, or the item count for lists (`3 items`). */
  value: string;
  field: FieldValue | null;
  /** Lists (lines, taxBreakdown) are shown as a count, not flattened. */
  count: number | null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * A readable one-liner for a `FieldValue`'s raw value: Money (`{amount, currency}`), identifiers and parties
 * (`{value}` / `{name}`), dates (`YYYY-MM-DD`), scalars, else compact JSON.
 */
export function formatCanonicalValue(value: unknown, locale?: string): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "string") return /^\d{4}-\d{2}-\d{2}$/.test(value) ? formatDate(value, locale) : value;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  if (Array.isArray(value)) return value.map((item) => formatCanonicalValue(item, locale)).join(", ");
  if (isRecord(value)) {
    if ("amount" in value && ("currency" in value || Object.keys(value).length === 1)) {
      const currency = typeof value.currency === "string" ? value.currency : null;
      return formatMoney(value.amount as number | string | null, currency, locale);
    }
    if (typeof value.name === "string") {
      const vat = typeof value.vatId === "string" ? ` (${value.vatId})` : "";
      return `${value.name}${vat}`;
    }
    if ("value" in value && (typeof value.value === "string" || typeof value.value === "number")) {
      const scheme = typeof value.scheme === "string" ? ` [${value.scheme}]` : "";
      return `${value.value}${scheme}`;
    }
    if ("start" in value || "end" in value) {
      return `${formatCanonicalValue(value.start, locale)} – ${formatCanonicalValue(value.end, locale)}`;
    }
    // Document numbers keep the printed form next to the normalised one; the normalised form is what matching uses.
    if ("verbatim" in value || "normalized" in value) {
      const shown = value.normalized ?? value.verbatim;
      return typeof shown === "string" || typeof shown === "number" ? String(shown) : "—";
    }
    // Flat records (payment terms): "key: value" pairs instead of JSON.
    const entries = Object.entries(value).filter(([, v]) => v !== null && v !== undefined);
    if (entries.length > 0 && entries.every(([, v]) => typeof v === "string" || typeof v === "number" || typeof v === "boolean")) {
      const text = entries.map(([k, v]) => `${k}: ${formatCanonicalValue(v, locale)}`).join(" · ");
      return text.length > 160 ? `${text.slice(0, 157)}…` : text;
    }
    try {
      const json = JSON.stringify(value);
      return json.length > 120 ? `${json.slice(0, 117)}…` : json;
    } catch {
      return String(value);
    }
  }
  return String(value);
}

function isValuelessField(value: unknown): value is Omit<FieldValue, "value"> {
  return isRecord(value) && !("value" in value) && typeof value.status === "string" && (fieldStatuses as readonly string[]).includes(value.status);
}

/**
 * The read-only summary of a canonical document (`DocumentDetail.canonical`): one row per top-level field, lists as
 * counts, unknown shapes as JSON. Order is the JSON's key order, which is the model's declaration order.
 */
export function canonicalRows(canonical: unknown, locale?: string): CanonicalRow[] {
  if (!isRecord(canonical)) return [];
  const rows: CanonicalRow[] = [];
  for (const [path, raw] of Object.entries(canonical)) {
    if (Array.isArray(raw)) {
      rows.push({ path, value: String(raw.length), field: null, count: raw.length });
    } else if (isFieldValue(raw)) {
      rows.push({ path, value: formatCanonicalValue(raw.value, locale), field: raw, count: null });
    } else if (isValuelessField(raw)) {
      // The API omits `value` when it is null (a Missing or Unreadable field).
      const field: FieldValue = { ...raw, value: null };
      rows.push({ path, value: "—", field, count: null });
    } else {
      rows.push({ path, value: formatCanonicalValue(raw, locale), field: null, count: null });
    }
  }
  return rows;
}

export const flagSeverityVariant: Readonly<Record<FlagSeverity, "outline" | "secondary" | "destructive" | "default">> = {
  Info: "outline",
  Warning: "secondary",
  Blocking: "destructive",
};
