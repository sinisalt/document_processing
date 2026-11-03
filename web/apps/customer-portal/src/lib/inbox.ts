import { inFlightStatuses, type DocumentStatus } from "@docproc/app-shell";
import { legacyTypeKeys, localized, type DocumentTypeDetail, type IndexedField } from "@docproc/review";

export const inboxTabs = ["needsReview", "processing", "approved", "rejected", "failed", "all"] as const;
export type InboxTab = (typeof inboxTabs)[number];

/** The `status` filter of `GET /documents` (comma list) each inbox tab stands for; `all` sends none. */
export const tabStatuses: Readonly<Record<InboxTab, readonly DocumentStatus[]>> = {
  needsReview: ["NeedsReview"],
  processing: [...inFlightStatuses, "Validated"],
  approved: ["Approved", "AutoApproved"],
  rejected: ["Rejected"],
  failed: ["Failed"],
  all: [],
};

export function statusFilterOf(tab: InboxTab): string | undefined {
  const statuses = tabStatuses[tab];
  return statuses.length > 0 ? statuses.join(",") : undefined;
}

export function isInboxTab(value: unknown): value is InboxTab {
  return typeof value === "string" && (inboxTabs as readonly string[]).includes(value);
}

/** One entry of the inbox's type filter. */
export interface TypeOption {
  key: string;
  /** The tenant's name of the type, when it is one of the tenant's types (a legacy type is named by the UI language). */
  name?: string;
}

/**
 * The types the inbox filters by (docs/11 §19 "types come from the API"): the legacy types every tenant has, in their
 * order, then the tenant's other types (an adopted price list, a type of its own) by name.
 */
export function typeOptions(types: readonly DocumentTypeDetail[] | undefined, language: string): TypeOption[] {
  const legacy = new Set<string>(legacyTypeKeys);
  const own = (Array.isArray(types) ? types : [])
    .filter((type) => !legacy.has(type.key))
    .map((type) => ({ key: type.key, name: localized(type.name, language) ?? type.key }))
    .sort((a, b) => a.name.localeCompare(b.name, language));
  return [...legacyTypeKeys.map((key) => ({ key })), ...own];
}

/** An indexed field the inbox offers as a filter and a column, with the types that have it. */
export interface IndexedFieldOption extends IndexedField {
  name: string;
  types: string[];
}

/** The most indexed fields the inbox asks for as columns (`fields=`, `DocumentEndpoints.MaxFieldColumns`). */
export const MAX_FIELD_COLUMNS = 10;

/**
 * The indexed fields of the selected type, or of every type when none is selected (one entry per path, named after the
 * first type that has it): what `?field.<path>=value` filters by.
 */
export function indexedFieldOptions(types: readonly DocumentTypeDetail[] | undefined, selectedType: string | undefined, language: string): IndexedFieldOption[] {
  const byPath = new Map<string, IndexedFieldOption>();
  for (const type of Array.isArray(types) ? types : []) {
    if (selectedType && type.key !== selectedType) continue;
    for (const field of type.indexedFields ?? []) {
      const known = byPath.get(field.path);
      if (known) known.types.push(type.key);
      else byPath.set(field.path, { ...field, name: localized(field.label, language) ?? field.path, types: [type.key] });
    }
  }
  return [...byPath.values()];
}

/** The `GET /documents` query parameter of a field filter: `field.<path>`, with the value trimmed; nothing when either is blank. */
export function fieldFilterQuery(path: string | undefined, value: string | undefined): Record<string, string> {
  const trimmed = value?.trim();
  return path && trimmed ? { [`field.${path}`]: trimmed } : {};
}

/** The dashboard's status groups: the tabs that count something. */
export const countedTabs: readonly InboxTab[] = ["needsReview", "processing", "approved", "rejected", "failed"];
