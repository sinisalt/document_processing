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

/** The decimal separator of a locale (`de-DE` ",", `en-US` "."); "," when the locale is unknown, as the API reads by default. */
export function decimalSeparatorOf(locale: string | null | undefined): "," | "." {
  if (!locale) return ",";
  try {
    return new Intl.NumberFormat(locale).formatToParts(1.5).find((p) => p.type === "decimal")?.value === "." ? "." : ",";
  } catch {
    return ",";
  }
}

/**
 * The minimum quantity a tier header names (`ab 10`, `from 50`, `>= 100`, `ab 1.000`, `ab 1 000`), read in `locale` as
 * the API reads printed numbers (docs/11 §8.6): spaces group thousands, and so does a separator that is repeated or
 * followed by exactly three digits, unless it is the locale's decimal separator; or null.
 */
export function tierQuantityOf(header: string | null | undefined, locale?: string | null): number | null {
  const match = /(\d(?:[\d.,\s\u00a0\u202f]*\d)?)/.exec(header ?? "");
  if (!match) return null;
  const text = match[1]!.replace(/[\s\u00a0\u202f]/g, "");
  const decimal = decimalSeparatorOf(locale);
  const lastDot = text.lastIndexOf(".");
  const lastComma = text.lastIndexOf(",");
  let separator: string | null;
  if (lastDot >= 0 && lastComma >= 0) separator = lastDot > lastComma ? "." : ",";
  else if (lastDot < 0 && lastComma < 0) separator = null;
  else {
    const sign = lastDot >= 0 ? "." : ",";
    const count = text.split(sign).length - 1;
    const after = text.length - text.lastIndexOf(sign) - 1;
    separator = count > 1 ? null : sign === decimal ? sign : after === 3 ? null : sign;
  }
  const at = separator === null ? -1 : text.lastIndexOf(separator);
  const whole = (at < 0 ? text : text.slice(0, at)).replace(/[.,]/g, "");
  const fraction = at < 0 ? "" : text.slice(at + 1);
  const value = Number(fraction ? `${whole}.${fraction}` : whole);
  return Number.isFinite(value) ? value : null;
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
  /**
   * Lines that are not rows wherever they are printed (a running page title, "Seite 1 von 14", "Übertrag"): the text
   * they start with, as the reviewer typed it or took it from a rejected line (`skipRows`). Absent in a state saved
   * before the tool had it.
   */
  skipLines?: string[];
  /** The table ends at a line that starts with one of these ("Ende der Preisliste", `stopAt`). */
  stopLines?: string[];
  /**
   * The band of the other pages that is read, in page units (`pageMargins`): from `top` (null: the top of the page)
   * to `bottom` (null: the bottom of the box, or the foot of the page for a box drawn around the header row only).
   */
  margins?: TableMargins;
  /** The locale the API read the values in (`de-DE`), for the quantity of a tier header. */
  locale?: string | null;
}

export interface TableMargins {
  top: number | null;
  bottom: number | null;
}

/** The most texts a list of skip or stop lines takes (the API takes 20 patterns of at most 200 characters). */
export const MAX_LINE_TEXTS = 20;

/**
 * The `skipRows`/`stopAt` pattern for a line the reviewer named by its text: a line that starts with that text, where
 * any run of digits stands for any number (so "Seite 1 von 14" also leaves out "Seite 2 von 14") and any run of spaces
 * for any spacing. Everything else is taken literally.
 */
export function linePattern(text: string): string {
  // At most MAX_LINE_TEXT_LENGTH characters of the text: escaped, a pattern stays within the API's 200 characters.
  const escaped = text
    .trim()
    .slice(0, MAX_LINE_TEXT_LENGTH)
    .trim()
    .split(/\s+/)
    .map((word) =>
      word
        .split(/(\d+)/)
        .map((part, i) => (i % 2 === 1 ? "\\d+" : part.replace(/[\\^$.|?*+()[\]{}]/g, "\\$&")))
        .join(""),
    )
    .join("\\s+");
  return `^${escaped}`;
}

/** The line a rejected line stands for: the line added last (a row's continuation line, or the line itself). */
export function rejectedLineText(line: Pick<TableRejectedLine, "text">): string {
  const parts = line.text.split(" / ");
  return (parts[parts.length - 1] ?? line.text).trim();
}

/** The most characters of a line's start a skip or stop text keeps (what `linePattern` reads of it). */
export const MAX_LINE_TEXT_LENGTH = 60;

/** The start of a line as a skip or stop text: trimmed, cut at a word boundary to `MAX_LINE_TEXT_LENGTH` characters. */
export function lineStart(text: string): string {
  const trimmed = text.trim().replace(/\s+/g, " ");
  if (trimmed.length <= MAX_LINE_TEXT_LENGTH) return trimmed;
  const cut = trimmed.slice(0, MAX_LINE_TEXT_LENGTH);
  const space = cut.lastIndexOf(" ");
  return (space > 0 ? cut.slice(0, space) : cut).trim();
}

/** `texts` with the start of `text` added (`lineStart`, once, at most `MAX_LINE_TEXTS`). */
export function addLineText(texts: readonly string[] | undefined, text: string): string[] {
  const list = [...(texts ?? [])];
  const trimmed = lineStart(text);
  if (trimmed === "" || list.includes(trimmed) || list.length >= MAX_LINE_TEXTS) return list;
  return [...list, trimmed];
}

/** The margins of a state (none in a state saved before the tool had them). */
export function marginsOf(state: Pick<TableToolState, "margins">): TableMargins {
  return { top: state.margins?.top ?? null, bottom: state.margins?.bottom ?? null };
}

/** A margin as the reviewer sees it, in percent of the page height (page units run from 0 to 1000). */
export function marginPercent(value: number | null): string {
  return value === null ? "" : String(Math.round(value) / 10);
}

/** A margin typed in percent of the page height, in page units; null for an empty or unreadable entry. */
export function marginFromPercent(text: string): number | null {
  const trimmed = text.trim().replace(",", ".");
  if (trimmed === "") return null;
  const value = Number(trimmed);
  if (!Number.isFinite(value)) return null;
  return Math.round(Math.min(100, Math.max(0, value)) * 10);
}

/** The `regions:table` body for the state: a preview (`write` false) or the write. */
export function tableRequest(state: TableToolState, write: boolean, reason?: string): ReadTableRequest {
  const margins = marginsOf(state);
  return {
    page: state.page,
    bBox: state.box,
    columns: state.columns.map((c) => ({ x0: c.x0, x1: c.x1, field: c.field ?? null, header: c.header ?? null, tierMinQuantity: c.tierMinQuantity ?? null })),
    headerRow: state.headerRow,
    rowAnchor: state.rowAnchor,
    path: state.path,
    pages: state.allPages ? `${state.page}-*` : String(state.page),
    groupHeader: state.groupHeadingTarget ? { target: state.groupHeadingTarget } : null,
    skipRows: state.skipLines && state.skipLines.length > 0 ? state.skipLines.map(linePattern) : null,
    stopAt: state.stopLines && state.stopLines.length > 0 ? state.stopLines.map(linePattern) : null,
    pageMargins: margins.top !== null || margins.bottom !== null ? margins : null,
    write,
    reason: write ? (reason ?? null) : null,
  };
}

/**
 * The row anchor after the reviewer re-targeted the columns: the group's own anchor (the definition's `anchor`, the
 * field every row has) once a column holds it, else the anchor kept while its column is still mapped, else the first
 * mapped field. The proposal picks its anchor from the columns it could name itself (on a German table only "Einheit"
 * perhaps), so without this a table read by unit is replayed and learned by unit.
 */
export function anchorAfterTargets(columns: readonly { field?: string | null }[], definitionAnchor: string | null | undefined, current: string | null): string | null {
  const mapped = columns.map((c) => c.field).filter((f): f is string => !!f);
  if (definitionAnchor && mapped.includes(definitionAnchor)) return definitionAnchor;
  if (current && mapped.includes(current)) return current;
  return mapped[0] ?? null;
}

/** Whether the mapping can be read: a row anchor whose column is mapped, at least one column with a field, and the margins in order. */
export function mappingProblem(state: Pick<TableToolState, "columns" | "rowAnchor" | "margins">): "noColumns" | "noAnchor" | "margins" | null {
  if (!state.columns.some((c) => c.field)) return "noColumns";
  if (state.rowAnchor && !state.columns.some((c) => c.field === state.rowAnchor)) return "noAnchor";
  const margins = marginsOf(state);
  if (margins.top !== null && margins.bottom !== null && margins.top >= margins.bottom) return "margins";
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
