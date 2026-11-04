import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  appendRow,
  articlePriceImportMap,
  compileFieldMap,
  compilePath,
  compileValue,
  fieldMapOf,
  fieldMapProblems,
  listChainOf,
  listSample,
  mapComment,
  moveRow,
  outputLists,
  readFieldMap,
  relativeSource,
  rowAt,
  updateRowAt,
  type FieldMap,
} from "./fieldMap";

/**
 * The compiled `article-price-import` map, committed so the server side can render it: `ValuesViewTests` checks that it
 * renders what the hand-written expression of docs/11 §13.10 renders. `UPDATE_FIXTURES=1 pnpm test` writes it again.
 */
const FIXTURE = join(__dirname, "../../../../../../fixtures/outputs/article-price-import.fieldmap.jsonata");

describe("compiling a field map", () => {
  it("writes paths plainly, odd names in backticks, and $root as it is", () => {
    expect(compilePath("supplier.attributes.erpVendorId")).toBe("supplier.attributes.erpVendorId");
    expect(compilePath("doc.ext.store-no")).toBe("doc.ext.`store-no`");
    expect(compilePath("$root.roles.validFrom")).toBe("$root.roles.validFrom");
  });

  it("applies fallback, lookup, formatter, constant when empty and required in that order", () => {
    expect(compileValue({ kind: "value", target: "x", source: "unit", lookup: { table: "erp-uom", default: "ST" } })).toBe('$table("erp-uom", unit, "ST")');
    expect(compileValue({ kind: "value", target: "x", source: "roles.validUntil", format: { kind: "date", pattern: "yyyyMMdd" }, whenEmpty: { type: "text", value: "99991231" } })).toBe('(roles.validUntil ? $fmtDate(roles.validUntil, "yyyyMMdd") : "99991231")');
    expect(compileValue({ kind: "value", target: "x", source: "lineValidFrom", fallback: "$root.roles.validFrom", format: { kind: "date", pattern: "yyyyMMdd" } })).toBe('$fmtDate((lineValidFrom ? lineValidFrom : $root.roles.validFrom), "yyyyMMdd")');
    expect(compileValue({ kind: "value", target: "x", source: "priceQuantity", whenEmpty: { type: "number", value: "1" } })).toBe("(priceQuantity ? priceQuantity : 1)");
    expect(compileValue({ kind: "value", target: "x", source: "total", format: { kind: "number", decimals: 2, decimalSeparator: ",", groupSeparator: "." } })).toBe('$fmtNum(total, 2, ",", ".")');
    expect(compileValue({ kind: "value", target: "x", source: "supplier.attributes.erpVendorId", required: 'No "id"' })).toBe('$required(supplier.attributes.erpVendorId, "No \\"id\\"")');
  });

  it("nests targets with dots, keeps the rows' order and writes lists of rows and of plain values", () => {
    const map: FieldMap = {
      version: 1,
      rows: [
        { kind: "value", target: "header.no", source: "roles.documentNumber" },
        { kind: "constant", target: "kind", value: { type: "text", value: "PL" } },
        { kind: "value", target: "header.currency", source: "roles.currency" },
        { kind: "list", target: "rows", source: "doc.lines", rows: [{ kind: "value", target: "sku", source: "supplierSku" }] },
        { kind: "list", target: "skus", source: "doc.lines", filter: 'status = "Discontinued"', rows: [{ kind: "value", target: "", source: "supplierSku" }] },
      ],
    };
    const body = compileFieldMap(map).split("\n").slice(1).join("\n");
    expect(body).toBe(
      [
        "(",
        "  $root := $;",
        "  {",
        '    "header": {',
        '      "no": roles.documentNumber,',
        '      "currency": roles.currency',
        "    },",
        '    "kind": "PL",',
        '    "rows": [ doc.lines.{',
        '      "sku": supplierSku',
        "    } ],",
        '    "skus": [ doc.lines[status = "Discontinued"].supplierSku ]',
        "  }",
        ")",
      ].join("\n"),
    );
  });

  it("carries the map in a comment that survives */ in a value and reads back", () => {
    const map: FieldMap = { version: 1, rows: [{ kind: "expression", target: "a", expression: '"x*/y"' }] };
    const expression = compileFieldMap(map);
    expect(mapComment(map)).not.toContain('x*/y"');
    expect(readFieldMap(expression)).toEqual(map);
    expect(fieldMapOf(expression)).toEqual({ map, inSync: true });
    expect(fieldMapOf(`${expression}\n`).inSync).toBe(false);
    expect(fieldMapOf("{ \"a\": 1 }")).toEqual({ map: null, inSync: false });
    expect(readFieldMap("/* docproc:fieldmap {not json} */\n{}")).toBeNull();
  });

  it("compiles docs/11 §13.10's article-price-import to the committed fixture", () => {
    const expression = compileFieldMap(articlePriceImportMap);
    if (process.env.UPDATE_FIXTURES === "1" || !existsSync(FIXTURE)) {
      mkdirSync(dirname(FIXTURE), { recursive: true });
      writeFileSync(FIXTURE, expression);
    }
    expect(readFileSync(FIXTURE, "utf8")).toBe(expression);
    expect(fieldMapProblems(articlePriceImportMap)).toEqual([]);
    // The parts §13.10 calls out: the filter with $not, $root for the list-level validity, tier prices as scales.
    expect(expression).toContain('"prices": [ doc.lines[$not(status = "Discontinued")].{');
    expect(expression).toContain('"validFrom": $fmtDate((lineValidFrom ? lineValidFrom : $root.roles.validFrom), "yyyyMMdd")');
    expect(expression).toContain('"scales": [ tiers.{');
    expect(expression).toContain('"delist": [ doc.lines[status = "Discontinued"].supplierSku ]');
  });
});

describe("checking a field map", () => {
  it("names the row of every problem", () => {
    const map: FieldMap = {
      version: 1,
      rows: [
        { kind: "value", target: "", source: "a" },
        { kind: "value", target: "x", source: "" },
        { kind: "value", target: "x", source: "b" },
        { kind: "value", target: "x.y", source: "c" },
        { kind: "constant", target: "n", value: { type: "number", value: "1,5" } },
        { kind: "expression", target: "e", expression: " " },
        { kind: "list", target: "l", source: "doc.lines", rows: [] },
        { kind: "list", target: "m", source: "doc.lines", rows: [{ kind: "value", target: "", source: "sku" }, { kind: "value", target: "q", source: "x`y" }] },
        { kind: "value", target: "f", source: "d", lookup: { table: "" }, format: { kind: "date", pattern: "" } },
      ],
    };
    expect(fieldMapProblems(map)).toEqual([
      { at: "0", code: "targetMissing" },
      { at: "1", code: "sourceMissing" },
      { at: "2", code: "targetDuplicate" },
      { at: "3", code: "targetConflict" },
      { at: "4", code: "numberInvalid" },
      { at: "5", code: "expressionMissing" },
      { at: "6", code: "listEmpty" },
      { at: "7.0", code: "targetMissing" },
      { at: "7.1", code: "sourceInvalid" },
      { at: "8", code: "lookupMissing" },
      { at: "8", code: "formatInvalid" },
    ]);
  });

  it("refuses number separators that would change the amount, as the renderer does", () => {
    const row = (format: { decimals: number; decimalSeparator?: string; groupSeparator?: string }): FieldMap => ({ version: 1, rows: [{ kind: "value", target: "x", source: "total", format: { kind: "number", ...format } }] });
    for (const bad of [
      { decimals: 2, decimalSeparator: "", groupSeparator: "" },
      { decimals: 2, decimalSeparator: ".", groupSeparator: "." },
      { decimals: 2, decimalSeparator: "0" },
      { decimals: 0, groupSeparator: "-" },
    ]) {
      expect(fieldMapProblems(row(bad))).toEqual([{ at: "0", code: "formatInvalid" }]);
    }
    for (const good of [{ decimals: 2, decimalSeparator: ",", groupSeparator: "." }, { decimals: 0, decimalSeparator: ".", groupSeparator: "." }, { decimals: 2 }, { decimals: 2, groupSeparator: " " }]) {
      expect(fieldMapProblems(row(good))).toEqual([]);
    }
  });
});

describe("editing rows", () => {
  const rows = articlePriceImportMap.rows;

  it("finds, replaces, removes, appends and moves rows by their place", () => {
    expect(rowAt(rows, "9.10")?.target).toBe("scales");
    expect(rowAt(rows, "9.10.0")?.target).toBe("fromQty");
    const renamed = updateRowAt(rows, "9.10.0", (row) => ({ ...row, target: "from" }));
    expect(rowAt(renamed, "9.10.0")?.target).toBe("from");
    expect(rowAt(rows, "9.10.0")?.target).toBe("fromQty");
    expect(updateRowAt(rows, "0", () => null)).toHaveLength(rows.length - 1);
    expect(rowAt(appendRow(rows, "9", { kind: "value", target: "tax", source: "taxRate" }), "9.11")?.target).toBe("tax");
    expect(rowAt(moveRow(rows, "9.1", -1), "9.0")?.target).toBe("ean");
    expect(moveRow(rows, "0", -1)).toEqual(rows);
  });

  it("makes a picked path relative to the lists the row lies in", () => {
    expect(listChainOf(rows, "9.10.0")).toEqual(["doc.lines", "doc.lines[].tiers"]);
    expect(relativeSource("doc.lines[].tiers[].minQuantity", listChainOf(rows, "9.10.0"))).toBe("minQuantity");
    expect(relativeSource("doc.lines[].price.amount", listChainOf(rows, "9.8"))).toBe("price.amount");
    expect(relativeSource("roles.validFrom", listChainOf(rows, "9.9"))).toBe("$root.roles.validFrom");
    expect(relativeSource("roles.validFrom", listChainOf(rows, "2"))).toBe("roles.validFrom");
    expect(relativeSource("doc.lines[].tiers", listChainOf(rows, "9.10"))).toBe("tiers");
  });
});

describe("source and output helpers", () => {
  it("merges the first rows of a list so a row without tiers does not hide them", () => {
    const view = { doc: { lines: [{ sku: "1", tiers: [] }, { sku: "2", tiers: [{ minQuantity: 10 }], gtin: "4006" }] } };
    expect(listSample(view, "doc.lines")).toEqual({ sku: "1", tiers: [{ minQuantity: 10 }], gtin: "4006" });
    expect(listSample(view, "doc.missing")).toBeUndefined();
  });

  it("counts the lists of an output: 412 prices with the tier prices as scales", () => {
    const prices = Array.from({ length: 412 }, (_, i) => ({ scales: i % 10 === 0 ? [{ fromQty: 10 }, { fromQty: 50 }] : [] }));
    expect(outputLists({ header: {}, prices, delist: [] })).toEqual([
      { path: "prices", items: 412 },
      { path: "prices[].scales", items: 84, inRows: 42 },
      { path: "delist", items: 0 },
    ]);
  });
});
