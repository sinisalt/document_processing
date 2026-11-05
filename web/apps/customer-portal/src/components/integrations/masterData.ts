import type { components } from "@docproc/api-client";

export type MasterDataRun = components["schemas"]["MasterDataRunDto"];
export type MasterDataSource = components["schemas"]["MasterDataSourceDto"];

/** What an import writes (docs/11 §14): the supplier's own fields and `attributes.<key>`. */
export const importTargets = ["name", "vatId", "taxId", "ibans", "currency"] as const;
/** The match keys in their default order (`SupplierImporter.MatchKeys.Default`). */
export const defaultMatchKeys = ["attributes.erpVendorId", "vatId", "iban"] as const;
export const conflictPolicies = ["externalWins", "localWins", "flag"] as const;
export type ConflictPolicy = (typeof conflictPolicies)[number];

/** One row of a run's report (`MasterDataRow`). */
export interface ReportRow {
  row: number;
  action: "create" | "update" | "unchanged" | "conflict" | "error" | string;
  supplierId: string | null;
  name: string | null;
  matchedBy: string | null;
  changes: string[];
  conflicts: string[];
  error: string | null;
}

export interface Report {
  created: number;
  updated: number;
  unchanged: number;
  conflicts: number;
  errors: number;
  rows: ReportRow[];
  failure: string | null;
}

/** A run's stored report, read defensively (the counts of the run when the report lacks them). */
export function reportOf(run: Pick<MasterDataRun, "report" | "created" | "updated" | "unchanged" | "conflicts" | "errors">): Report {
  const raw = run.report && typeof run.report === "object" ? (run.report as Record<string, unknown>) : {};
  const rows = Array.isArray(raw.rows) ? (raw.rows as ReportRow[]).map((r) => ({ ...r, changes: r.changes ?? [], conflicts: r.conflicts ?? [] })) : [];
  return {
    created: Number(raw.created ?? run.created),
    updated: Number(raw.updated ?? run.updated),
    unchanged: Number(raw.unchanged ?? run.unchanged),
    conflicts: Number(raw.conflicts ?? run.conflicts),
    errors: Number(raw.errors ?? run.errors),
    rows,
    failure: typeof raw.failure === "string" ? raw.failure : null,
  };
}

/** The header of a CSV text: its first line split at the delimiter the line uses most (`,` or `;`), quotes removed. */
export function csvHeader(content: string): string[] {
  const first = content.replace(/^﻿/, "").split(/\r?\n/, 1)[0] ?? "";
  if (!first.trim()) return [];
  const delimiter = (first.match(/;/g)?.length ?? 0) > (first.match(/,/g)?.length ?? 0) ? ";" : ",";
  return first.split(delimiter).map((cell) => cell.trim().replace(/^"(.*)"$/, "$1"));
}

const guesses: Array<[string, RegExp]> = [
  ["name", /^(name|lieferant|supplier|firma|company)$/i],
  ["vatId", /^(vat ?id|vat|ust-?id(nr)?|ust-?idnr\.?|umsatzsteuer-?id)$/i],
  ["taxId", /^(tax ?id|steuernummer|steuer-?nr\.?)$/i],
  ["ibans", /^(iban|ibans)$/i],
  ["currency", /^(currency|währung|waehrung)$/i],
  ["attributes.erpVendorId", /^(erp ?vendor ?id|vendor ?(no|number|id)|kreditor(en)?(nummer|nr\.?)?)$/i],
];

/** A first column map for a header (target → header name), guessed from common names in English and German. */
export function guessColumns(header: readonly string[]): Record<string, string> {
  const columns: Record<string, string> = {};
  for (const [target, pattern] of guesses) {
    const hit = header.find((h) => pattern.test(h.trim()));
    if (hit && !Object.values(columns).includes(hit)) columns[target] = hit;
  }
  return columns;
}

/** Whether a target may be written by an import: the supplier's own fields or `attributes.<key>`. */
export function isImportTarget(target: string): boolean {
  return (importTargets as readonly string[]).includes(target) || /^attributes\.[A-Za-z][A-Za-z0-9_]{0,63}$/.test(target);
}

/** The body of `POST /suppliers:import`. */
export function importBody(input: { format: "csv" | "json"; content: string; columns: Record<string, string>; matchKeys: readonly string[]; policy: Record<string, ConflictPolicy>; dryRun: boolean }): {
  format: string;
  content: string | null;
  records: unknown;
  columns: Record<string, string> | null;
  matchKeys: string[];
  conflictPolicy: Record<string, string> | null;
  dryRun: boolean;
} {
  let records: unknown = null;
  if (input.format === "json") {
    try {
      records = JSON.parse(input.content);
    } catch {
      records = null;
    }
  }
  return {
    format: input.format,
    content: input.format === "csv" ? input.content : null,
    records,
    columns: input.format === "csv" ? input.columns : null,
    matchKeys: [...input.matchKeys],
    conflictPolicy: Object.keys(input.policy).length > 0 ? input.policy : null,
    dryRun: input.dryRun,
  };
}
