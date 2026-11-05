import { describe, expect, it } from "vitest";
import { templates } from "../test/templates";
import {
  addDivider,
  anchorAfterTargets,
  addLineText,
  decimalSeparatorOf,
  defaultTablePath,
  dividers,
  linePattern,
  lineStart,
  mappingProblem,
  marginFromPercent,
  marginPercent,
  moveDivider,
  pageReport,
  rejectedLineText,
  removeDivider,
  reportTotals,
  rowFieldsOf,
  stripEntries,
  tableRequest,
  targetOf,
  tierQuantityOf,
  withTarget,
  type TableColumn,
  type TableReport,
  type TableToolState,
} from "./tableTool";

const columns: TableColumn[] = [
  { x0: 50, x1: 150, field: "supplierSku", header: "Art.-Nr." },
  { x0: 150, x1: 500, field: "description", header: "Bezeichnung" },
  { x0: 500, x1: 650, field: "price", header: "Preis" },
  { x0: 650, x1: 800, field: null, header: "ab 10", tierMinQuantity: 10 },
];

const page = (n: number, rows: number, rejected = 0) => ({
  page: n,
  headerFound: n !== 3,
  rows,
  rejected,
  rejectedLines: rejected ? [{ page: n, text: "104 Wein 1,2x", bBox: { x0: 50, y0: 400, x1: 700, y1: 410 }, reason: "unreadable", message: "'1,2x' is not an amount.", field: "price" }] : [],
  skippedLines: 0,
  groupHeadings: 0,
  continuationLines: 0,
  columns: [{ x0: 52, x1: 150 }],
  columnsFrom: n === 1 ? "mapping" : n === 3 ? "previousHeader" : "header",
  firstRows: [],
});

const report: TableReport = { rows: 52, rejectedRows: 1, pages: [1, 2, 3], pagesWithoutRows: [3], stoppedOnPage: null, perPage: [page(1, 22), page(2, 30, 1), page(3, 0)] };

describe("table tool", () => {
  it("lists, moves, adds and removes dividers", () => {
    expect(dividers(columns)).toEqual([50, 150, 500, 650, 800]);
    const moved = moveDivider(columns, 2, 480);
    expect(dividers(moved)).toEqual([50, 150, 480, 650, 800]);
    expect(moved[1]!.x1).toBe(480);
    expect(moved[2]!.x0).toBe(480);
    // Kept between its neighbours.
    expect(dividers(moveDivider(columns, 1, 10))).toEqual([50, 56, 500, 650, 800]);
    expect(dividers(moveDivider(columns, 4, 2000))).toEqual([50, 150, 500, 650, 1000]);

    const added = addDivider(columns, 300);
    expect(dividers(added)).toEqual([50, 150, 300, 500, 650, 800]);
    expect(added[1]!.field).toBe("description");
    expect(added[2]!.field).toBeNull();
    expect(addDivider(columns, 151)).toEqual(columns);

    const merged = removeDivider(columns, 1);
    expect(dividers(merged)).toEqual([50, 500, 650, 800]);
    expect(merged[0]).toMatchObject({ field: "supplierSku", header: "Art.-Nr. Bezeichnung" });
    expect(removeDivider(columns, 0)).toEqual(columns);
  });

  it("sets a column to a field, a tier price or nothing", () => {
    expect(targetOf(columns[0]!)).toEqual({ kind: "field", key: "supplierSku" });
    expect(targetOf(columns[3]!)).toEqual({ kind: "tier", minQuantity: 10 });
    expect(targetOf({ x0: 0, x1: 1 })).toEqual({ kind: "none" });
    expect(withTarget(columns[3]!, { kind: "field", key: "packDescription" })).toMatchObject({ field: "packDescription", tierMinQuantity: null });
    expect(withTarget(columns[0]!, { kind: "tier", minQuantity: 50 })).toMatchObject({ field: null, tierMinQuantity: 50 });
    expect(tierQuantityOf("ab 50")).toBe(50);
    expect(tierQuantityOf("from 2,5 kg")).toBe(2.5);
    expect(tierQuantityOf("Preis")).toBeNull();
  });

  it("reads a tier header's whole quantity in the locale the API read the page in", () => {
    expect(decimalSeparatorOf("de-DE")).toBe(",");
    expect(decimalSeparatorOf("en-US")).toBe(".");
    expect(decimalSeparatorOf(null)).toBe(",");
    expect(decimalSeparatorOf("not a locale!")).toBe(",");
    expect(tierQuantityOf("ab 1.000", "de-DE")).toBe(1000);
    expect(tierQuantityOf("ab 1 000 Stück", "de-DE")).toBe(1000);
    expect(tierQuantityOf("ab 1.000")).toBe(1000);
    expect(tierQuantityOf("ab 2,5 kg", "de-DE")).toBe(2.5);
    expect(tierQuantityOf("from 1,000", "en-US")).toBe(1000);
    expect(tierQuantityOf("from 1.5", "en-US")).toBe(1.5);
    expect(tierQuantityOf("ab 1.000", "en-US")).toBe(1);
    expect(tierQuantityOf("ab 1.000.000", "en-US")).toBe(1000000);
    expect(tierQuantityOf("ab 1.234,5", "en-US")).toBe(1234.5);
  });

  it("turns a line the reviewer names into a skip or stop pattern (any number stands for any number)", () => {
    expect(linePattern("Seite 1 von 14")).toBe("^Seite\\s+\\d+\\s+von\\s+\\d+");
    expect(linePattern("  Übertrag:  ")).toBe("^Übertrag:");
    expect(linePattern("Nordwind GmbH · (Preise) 2027*")).toBe("^Nordwind\\s+GmbH\\s+·\\s+\\(Preise\\)\\s+\\d+\\*");
    const pattern = new RegExp(linePattern("Seite 1 von 14"));
    expect(pattern.test("Seite 12 von 14")).toBe(true);
    expect(pattern.test("Seitenschneider 3")).toBe(false);
    expect(new RegExp(linePattern("a.b")).test("axb")).toBe(false);
    expect(linePattern("x".repeat(500)).length).toBeLessThanOrEqual(200);
    expect(linePattern("[".repeat(500)).length).toBeLessThanOrEqual(200);
    expect(rejectedLineText({ text: "104999 Rioja / Seite 3 von 14" })).toBe("Seite 3 von 14");
    expect(rejectedLineText({ text: "Seite 3 von 14" })).toBe("Seite 3 von 14");
    expect(addLineText(undefined, " Seite 1 ")).toEqual(["Seite 1"]);
    expect(addLineText(["Seite 1"], "Seite 1")).toEqual(["Seite 1"]);
    expect(addLineText(["a"], "  ")).toEqual(["a"]);
    expect(addLineText(Array.from({ length: 20 }, (_, i) => `l${i}`), "more")).toHaveLength(20);
    // A long footer line is kept by its start, cut at a word: what the pattern reads of it anyway.
    const footer = "Nordwind Großhandel GmbH · Amtsgericht Hamburg HRB 99812 · USt-IdNr. DE312456789 · IBAN DE44 2005";
    expect(addLineText([], footer)).toEqual(["Nordwind Großhandel GmbH · Amtsgericht Hamburg HRB 99812 ·"]);
    expect(lineStart("  a   b ")).toBe("a b");
    expect(new RegExp(linePattern(addLineText([], footer)[0]!)).test(footer)).toBe(true);
  });

  it("shows and reads the page margins in percent of the page height", () => {
    expect(marginPercent(null)).toBe("");
    expect(marginPercent(935)).toBe("93.5");
    expect(marginFromPercent("93,5")).toBe(935);
    expect(marginFromPercent("120")).toBe(1000);
    expect(marginFromPercent("")).toBeNull();
    expect(marginFromPercent("abc")).toBeNull();
  });

  it("offers the price list's row fields, its tiers and its category", () => {
    expect(defaultTablePath(templates.priceList)).toBe("lines");
    const { fields, hasTiers, hasCategory } = rowFieldsOf(templates.priceList, "lines");
    expect(fields.map((f) => f.key)).toContain("supplierSku");
    expect(fields.map((f) => f.key)).toContain("packDescription");
    expect(fields.map((f) => f.key)).not.toContain("tiers");
    expect(fields.map((f) => f.key)).not.toContain("ext");
    expect(hasTiers).toBe(true);
    expect(hasCategory).toBe(true);
    expect(rowFieldsOf(templates.priceList, "nothing").fields).toEqual([]);
  });

  it("builds the regions:table request: apply to all pages, group headings, the write", () => {
    const state: TableToolState = { page: 1, box: { x0: 45, y0: 280, x1: 945, y1: 935 }, columns, headerRow: { y0: 285, y1: 297 }, rowAnchor: "supplierSku", path: "lines", allPages: true, groupHeadingTarget: "category" };
    const preview = tableRequest(state, false);
    expect(preview).toMatchObject({ page: 1, pages: "1-*", groupHeader: { target: "category" }, rowAnchor: "supplierSku", path: "lines", write: false, reason: null });
    expect(preview.columns).toHaveLength(4);
    expect(preview.columns![3]).toMatchObject({ field: null, tierMinQuantity: 10 });
    expect(tableRequest({ ...state, allPages: false, groupHeadingTarget: null }, true, "captured")).toMatchObject({ pages: "1", groupHeader: null, write: true, reason: "captured" });
    expect(mappingProblem(state)).toBeNull();
    expect(mappingProblem({ ...state, columns: columns.map((c) => ({ ...c, field: null })) })).toBe("noColumns");
    expect(mappingProblem({ ...state, columns: columns.slice(1) })).toBe("noAnchor");
    // Nothing set: no skip, stop or margins in the request (a state saved before the tool had them reads the same).
    expect(preview).toMatchObject({ skipRows: null, stopAt: null, pageMargins: null });
    expect(tableRequest({ ...state, skipLines: [], stopLines: [], margins: { top: null, bottom: null } }, false)).toMatchObject({ skipRows: null, stopAt: null, pageMargins: null });
  });

  it("sends the lines that are not rows: skip and stop lines and the margins of the other pages", () => {
    const state: TableToolState = {
      page: 1,
      box: { x0: 45, y0: 280, x1: 945, y1: 300 },
      columns,
      headerRow: { y0: 285, y1: 297 },
      rowAnchor: "supplierSku",
      path: "lines",
      allPages: true,
      groupHeadingTarget: null,
      skipLines: ["Seite 1 von 14", "Nordwind Preisliste 2027"],
      stopLines: ["Ende der Preisliste"],
      margins: { top: 50, bottom: null },
    };
    expect(tableRequest(state, false)).toMatchObject({
      skipRows: ["^Seite\\s+\\d+\\s+von\\s+\\d+", "^Nordwind\\s+Preisliste\\s+\\d+"],
      stopAt: ["^Ende\\s+der\\s+Preisliste"],
      pageMargins: { top: 50, bottom: null },
    });
    expect(mappingProblem(state)).toBeNull();
    expect(mappingProblem({ ...state, margins: { top: 600, bottom: 400 } })).toBe("margins");
  });

  it("turns the report into the page strip", () => {
    expect(stripEntries(report)).toEqual([
      { page: 1, rows: 22, rejected: 0, headerFound: true, columnsFrom: "mapping", status: "ok" },
      { page: 2, rows: 30, rejected: 1, headerFound: true, columnsFrom: "header", status: "rejected" },
      { page: 3, rows: 0, rejected: 0, headerFound: false, columnsFrom: "previousHeader", status: "empty" },
    ]);
    expect(reportTotals(report)).toEqual({ rows: 52, rejected: 1, pages: 3, pagesWithoutRows: 1 });
    expect(pageReport(report, 2)?.rejectedLines).toHaveLength(1);
    expect(pageReport(report, 9)).toBeNull();
    expect(stripEntries(null)).toEqual([]);
  });
});

describe("the row anchor after re-targeting columns", () => {
  it("takes the group's own anchor once a column holds it, else keeps a mapped one, else the first mapped field", () => {
    const columns = (fields: (string | null)[]) => fields.map((field) => ({ field }));
    // The proposal of a German table named only "Einheit", so it anchored on unit; the reviewer then named the rest.
    expect(anchorAfterTargets(columns(["supplierSku", "description", "quantity", "unit"]), "description", "unit")).toBe("description");
    expect(anchorAfterTargets(columns(["supplierSku", null, "quantity", "unit"]), "description", "unit")).toBe("unit");
    expect(anchorAfterTargets(columns(["supplierSku", null, "quantity"]), "description", "unit")).toBe("supplierSku");
    expect(anchorAfterTargets(columns([null, null]), "description", "unit")).toBeNull();
    expect(anchorAfterTargets(columns(["price"]), undefined, null)).toBe("price");
  });
});
