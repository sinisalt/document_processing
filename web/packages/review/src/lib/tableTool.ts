import type { components } from "@docproc/api-client";
import type { FormSpec, FormSpecField } from "./formSpec";
import type { Box } from "./mapMode";

/**
 * The table tool (docs/11 §12.4): a box drawn around the table on one page, the column dividers the API proposes, the
 * reviewer's field per column, and "apply to all pages", answered by the table report the page strip shows. The pure
 * parts: dividers, the request, the strip.
 */
export type TableColumn = components["schemas"]["TableColumnDto"];
export type TableBand = components["schemas"]["TableBandDto"];
export type TableReport = components["schemas"]["TableReportDto"];
export type TablePageReport = components["schemas"]["TablePageReportDto"];
export type TableRejectedLine = components["schemas"]["TableRejectedLineDto"];
export type ReadTableRequest = components["schemas"]["ReadTableRequest"];
export type ReadTableResponse = components["schemas"]["ReadTableResponse"];
export type StoredTableReport = components["schemas"]["DocumentTableReportDto"];

/** The narrowest a column may get when a divider is moved (page units). */
export const MIN_COLUMN_WIDTH = 6;

const num = (value: number | string | null | undefined): number => (typeof value === "number" ? value : Number(value ?? 0));

/** Every divider of the columns, left to right: the left edge of each column and the right edge of the last. */
export function dividers(columns: readonly Pick<TableColumn, "x0" | "x1">[]): number[] {
  if (columns.length === 0) return [];
  return [...columns.map((c) => num(c.x0)), num(columns[columns.length - 1]!.x1)];
}

/** Divider `index` (0 = the table's left edge) moved to `x`, kept between its neighbours. */
export function moveDivider(columns: readonly TableColumn[], index: number, x: number): TableColumn[] {
  const lines = dividers(columns);
  if (index < 0 || index >= lines.length) return [...columns];
  const low = index === 0 ? 0 : lines[index - 1]! + MIN_COLUMN_WIDTH;
  const high = index === lines.length - 1 ? 1000 : lines[index + 1]! - MIN_COLUMN_WIDTH;
  const at = Math.round(Math.min(high, Math.max(low, x)) * 100) / 100;
  return columns.map((column, i) => {
    if (i === index) return { ...column, x0: at };
    if (i === index - 1) return { ...column, x1: at };
    return column;
  });
}

/** A new divider at `x` splits the column under it; the right half fills no field until the reviewer names one. */
export function addDivider(columns: readonly TableColumn[], x: number): TableColumn[] {
  const index = columns.findIndex((c) => x > num(c.x0) + MIN_COLUMN_WIDTH && x < num(c.x1) - MIN_COLUMN_WIDTH);
  if (index < 0) return [...columns];
  const column = columns[index]!;
  const at = Math.round(x * 100) / 100;
  return [...columns.slice(0, index), { ...column, x1: at }, { x0: at, x1: column.x1, field: null, header: null, tierMinQuantity: null }, ...columns.slice(index + 1)];
}

/** Inner divider `index` removed: the two columns beside it become one, which keeps the left one's field (or else the right one's). */
export function removeDivider(columns: readonly TableColumn[], index: number): TableColumn[] {
  if (index <= 0 || index >= columns.length) return [...columns];
  const left = columns[index - 1]!;
  const right = columns[index]!;
  const merged: TableColumn = {
    x0: left.x0,
    x1: right.x1,
    field: left.field ?? right.field ?? null,
    header: [left.header, right.header].filter(Boolean).join(" ") || null,
    tierMinQuantity: left.field ? null : (left.tierMinQuantity ?? (right.field ? null : right.tierMinQuantity) ?? null),
  };
  return [...columns.slice(0, index - 1), merged, ...columns.slice(index + 1)];
}

/** What a column is set to fill, as the column select's value. */
export type ColumnTarget = { kind: "field"; key: string } | { kind: "tier"; minQuantity: number } | { kind: "none" };

export function targetOf(column: TableColumn): ColumnTarget {
  if (column.field) return { kind: "field", key: column.field };
  if (column.tierMinQuantity !== null && column.tierMinQuantity !== undefined) return { kind: "tier", minQuantity: num(column.tierMinQuantity) };
  return { kind: "none" };
}

/** The minimum quantity a tier header names (`ab 10`, `from 50`, `>= 100`), or null. */
export function tierQuantityOf(header: string | null | undefined): number | null {
  const match = /(\d+(?:[.,]\d+)?)/.exec(header ?? "");
  return match ? Number(match[1]!.replace(",", ".")) : null;
}

export function withTarget(column: TableColumn, target: ColumnTarget): TableColumn {
  switch (target.kind) {
    case "field":
      return { ...column, field: target.key, tierMinQuantity: null };
    case "tier":
      return { ...column, field: null, tierMinQuantity: target.minQuantity };
    default:
      return { ...column, field: null, tierMinQuantity: null };
  }
}

/** The group's definition field (`lines`) in the form spec, and its row fields a column can fill (scalars only). */
export function rowFieldsOf(spec: FormSpec | null | undefined, path: string): { group: FormSpecField | null; fields: FormSpecField[]; hasTiers: boolean; hasCategory: boolean } {
  const group = spec?.fields.find((f) => f.path === path && f.kind === "array") ?? null;
  const children = group?.children ?? [];
  const fields = children.filter((f) => f.kind !== "array" && f.kind !== "bag" && !f.readOnly && f.provenance !== false);
  return { group, fields, hasTiers: children.some((f) => f.key === "tiers" && f.kind === "array"), hasCategory: children.some((f) => f.key === "category") };
}

/** The type's repeating group a table fills by default (the line items): the first array of the form spec. */
export function defaultTablePath(spec: FormSpec | null | undefined): string | null {
  return spec?.fields.find((f) => f.kind === "array" && !f.path.includes("["))?.path ?? null;
}

export interface TableToolState {
  page: number;
  box: Box;
  columns: TableColumn[];
  headerRow: TableBand | null;
  rowAnchor: string | null;
  path: string;
  /** "Apply to all pages" (`pages: "1-*"`), else only the page the table was drawn on. */
  allPages: boolean;
  /** Group headings fill this row field (`category`), or none. */
  groupHeadingTarget: string | null;
}

/** The `regions:table` body for the state: a preview (`write` false) or the write. */
export function tableRequest(state: TableToolState, write: boolean, reason?: string): ReadTableRequest {
  return {
    page: state.page,
    bBox: state.box,
    columns: state.columns.map((c) => ({ x0: c.x0, x1: c.x1, field: c.field ?? null, header: c.header ?? null, tierMinQuantity: c.tierMinQuantity ?? null })),
    headerRow: state.headerRow,
    rowAnchor: state.rowAnchor,
    path: state.path,
    pages: state.allPages ? `${state.page}-*` : String(state.page),
    groupHeader: state.groupHeadingTarget ? { target: state.groupHeadingTarget } : null,
    write,
    reason: write ? (reason ?? null) : null,
  };
}

/** Whether the mapping can be read: a row anchor whose column is mapped, and at least one column with a field. */
export function mappingProblem(state: Pick<TableToolState, "columns" | "rowAnchor">): "noColumns" | "noAnchor" | null {
  if (!state.columns.some((c) => c.field)) return "noColumns";
  if (state.rowAnchor && !state.columns.some((c) => c.field === state.rowAnchor)) return "noAnchor";
  return null;
}

export interface StripEntry {
  page: number;
  rows: number;
  rejected: number;
  headerFound: boolean;
  columnsFrom: string;
  status: "ok" | "rejected" | "empty";
}

/** One chip of the page strip per page read (docs/11 §12.4: "p. 1: 31 rows; p. 7: 2 rejected; p. 12: no rows"). */
export function stripEntries(report: TableReport | null | undefined): StripEntry[] {
  return (report?.perPage ?? []).map((p) => {
    const rows = num(p.rows);
    const rejected = num(p.rejected);
    return { page: num(p.page), rows, rejected, headerFound: p.headerFound, columnsFrom: p.columnsFrom, status: rejected > 0 ? "rejected" : rows === 0 ? "empty" : "ok" };
  });
}

/** The page report of one page, for its dividers and rejected lines. */
export function pageReport(report: TableReport | null | undefined, page: number): TablePageReport | null {
  return report?.perPage.find((p) => num(p.page) === page) ?? null;
}

export function reportTotals(report: TableReport | null | undefined): { rows: number; rejected: number; pages: number; pagesWithoutRows: number } {
  return { rows: num(report?.rows), rejected: num(report?.rejectedRows), pages: report?.pages.length ?? 0, pagesWithoutRows: report?.pagesWithoutRows.length ?? 0 };
}
