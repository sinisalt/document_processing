import { describe, expect, it } from "vitest";
import { bboxParam, boxFromDrag, candidateKindsFor, editableFromCandidate, grow, isClick, nextEmptyRequired, pickCandidate, sourceOf, union, wordAt, type ValueCandidate } from "./mapMode";
import { specsFromDefinition, type FieldSpec } from "./formSpec";
import { toFormValues } from "./values";
import { templates } from "../test/templates";

const candidates: ValueCandidate[] = [
  { kind: "date", value: "2027-01-01" },
  { kind: "decimal", value: 1 },
  { kind: "identifier", value: "01.01.2027" },
  { kind: "text", value: "01.01.2027" },
];

const spec = (patch: Partial<FieldSpec>): FieldSpec => ({ path: "x", fieldPath: "x", kind: "string", ...patch });

describe("map mode", () => {
  it("turns a drag into a box in page units and tells a click from a drag", () => {
    expect(boxFromDrag({ x: 300, y: 200 }, { x: 100, y: 250 })).toEqual({ x0: 100, y0: 200, x1: 300, y1: 250 });
    expect(boxFromDrag({ x: -5, y: 990 }, { x: 20, y: 1200 })).toEqual({ x0: 0, y0: 990, x1: 20, y1: 1000 });
    expect(isClick({ x0: 10, y0: 10, x1: 12, y1: 13 })).toBe(true);
    expect(isClick({ x0: 10, y0: 10, x1: 40, y1: 13 })).toBe(false);
  });

  it("hit-tests the smallest word box under a point and joins boxes", () => {
    const words = [
      { box: { x0: 0, y0: 0, x1: 500, y1: 20 } },
      { box: { x0: 100, y0: 5, x1: 140, y1: 15 } },
    ];
    expect(wordAt(words, { x: 120, y: 10 })).toBe(1);
    expect(wordAt(words, { x: 300, y: 10 })).toBe(0);
    expect(wordAt(words, { x: 300, y: 80 })).toBe(-1);
    expect(union([{ x0: 10, y0: 10, x1: 20, y1: 20 }, { x0: 5, y0: 15, x1: 30, y1: 18 }])).toEqual({ x0: 5, y0: 10, x1: 30, y1: 20 });
    expect(union([])).toBeNull();
    expect(grow({ x0: 0.5, y0: 10, x1: 20, y1: 999.5 })).toEqual({ x0: 0, y0: 9, x1: 21, y1: 1000 });
    expect(bboxParam({ x0: 1.234, y0: 2, x1: 3.5, y1: 4.005 })).toBe("1.23,2,3.5,4.01");
  });

  it("picks the reading of the field's kind (docs/11 §12.3: 28.10.2026 → 2026-10-28)", () => {
    expect(pickCandidate(spec({ kind: "date", definitionKind: "date" }), candidates)?.value).toBe("2027-01-01");
    expect(pickCandidate(spec({ kind: "identifier", definitionKind: "identifier" }), candidates)?.kind).toBe("identifier");
    expect(pickCandidate(spec({ kind: "number", definitionKind: "decimal" }), candidates)?.kind).toBe("decimal");
    expect(pickCandidate(spec({ kind: "money", definitionKind: "money" }), [{ kind: "decimal", value: 9.38 }])?.value).toBe(9.38);
    expect(pickCandidate(spec({ kind: "date" }), [{ kind: "text", value: "soon" }])).toBeNull();
    expect(candidateKindsFor("percent", "number")).toEqual(["percent", "decimal"]);
    expect(candidateKindsFor(undefined, "string")).toEqual(["text", "identifier"]);
  });

  it("puts a reading into the form as the field's editable text", () => {
    expect(editableFromCandidate(spec({ kind: "money" }), { kind: "money", value: { amount: 9.38, currency: "EUR" } }, "de")).toBe("9,38");
    expect(editableFromCandidate(spec({ kind: "money" }), { kind: "decimal", value: 9.38 }, "en")).toBe("9.38");
    expect(editableFromCandidate(spec({ kind: "date" }), { kind: "date", value: "2027-01-01" })).toBe("2027-01-01");
    expect(editableFromCandidate(spec({ kind: "identifier" }), { kind: "identifier", value: "PL-2027-01" })).toBe("PL-2027-01");
    expect(editableFromCandidate(spec({ kind: "number" }), { kind: "decimal", value: 412 }, "de")).toBe("412");
    // A code list takes the code whose code or label the text names.
    const scope = spec({ kind: "enum", options: ["Full", "Changes"], optionLabels: { Full: { en: "Full list", de: "Gesamtliste" } } });
    expect(editableFromCandidate(scope, { kind: "text", value: "Gesamtliste" })).toBe("Full");
    expect(editableFromCandidate(scope, { kind: "text", value: "changes" })).toBe("Changes");
    expect(editableFromCandidate(scope, { kind: "text", value: "Teilliste" })).toBe("Teilliste");
  });

  it("records where a value was read: page, box, the text layer's words and the printed text", () => {
    const read = { bBox: { x0: 1, y0: 2, x1: 3, y1: 4 }, text: "01.01.2027", words: [{ index: 17, text: "01.01.2027", bBox: { x0: 1, y0: 2, x1: 3, y1: 4 } }] };
    expect(sourceOf(1, read, "word")).toEqual({ method: "click", page: 1, bBox: read.bBox, words: [[1, 17]], rawText: "01.01.2027" });
    expect(sourceOf(2, read, "region").method).toBe("region");
    expect(sourceOf(2, { ...read, words: [{ index: null, text: "x", bBox: read.bBox }] }, "ocr")).toMatchObject({ method: "ocr", words: null });
  });

  it("moves on to the next empty required field, skipping rows and wrapping around", () => {
    const specs: FieldSpec[] = [
      spec({ path: "priceListNumber", required: false }),
      spec({ path: "validFrom", kind: "date", required: true }),
      spec({ path: "currency", required: true }),
      spec({ path: "lines[0].price", fieldPath: "lines[0].price", kind: "money", required: true }),
      spec({ path: "supplier.name", fieldPath: "supplier", required: true }),
    ];
    expect(nextEmptyRequired(specs, { validFrom: "", currency: "", supplier: { name: "" } }, null)?.path).toBe("validFrom");
    expect(nextEmptyRequired(specs, { validFrom: "2027-01-01", currency: "", supplier: { name: "" } }, "validFrom")?.path).toBe("currency");
    expect(nextEmptyRequired(specs, { validFrom: "", currency: "EUR", supplier: { name: "Nordwind" } }, "supplier.name")?.path).toBe("validFrom");
    expect(nextEmptyRequired(specs, { validFrom: "2027-01-01", currency: "EUR", supplier: { name: "Nordwind" } }, "currency")).toBeNull();
  });

  it("counts the price list's required supplier party as its first member until a member holds a value (the form spec std.price-list compiles to)", () => {
    // The compiled form spec marks the party required, not its members: Supplier > Name shows no "*".
    const { specs } = specsFromDefinition(templates.priceList, {}, { schemaVersion: 2 });
    const name = specs.find((s) => s.path === "supplier.name")!;
    expect(name.required).toBeUndefined();
    expect(name.inRequired).toBe(true);
    expect(specs.find((s) => s.path === "customer.name")?.inRequired).toBeUndefined();
    const values = (patch: Record<string, unknown>) => ({ ...toFormValues({}, specs), ...patch });
    const empty = values({});
    // The demo of docs/09: Valid from, then Currency (Price list no. is optional), then Supplier > Name.
    expect(nextEmptyRequired(specs, empty, null)?.path).toBe("validFrom");
    expect(nextEmptyRequired(specs, values({ validFrom: "2027-01-01" }), "validFrom")?.path).toBe("currency");
    expect(nextEmptyRequired(specs, values({ validFrom: "2027-01-01", priceListNumber: "PL-2027-01" }), "priceListNumber")?.path).toBe("currency");
    const header = values({ validFrom: "2027-01-01", priceListNumber: "PL-2027-01", currency: "EUR" });
    expect(nextEmptyRequired(specs, header, "currency")?.path).toBe("supplier.name");
    // Any member of the party holding a value fills it; from inside the party, Enter goes on past it.
    const withVat = { ...header, supplier: { ...(header.supplier as object), vatId: "DE123456789" } };
    expect(nextEmptyRequired(specs, withVat, "currency")).toBeNull();
    expect(nextEmptyRequired(specs, { ...empty, currency: "EUR" }, "supplier.vatId")?.path).toBe("validFrom");
    const done = { ...header, supplier: { ...(header.supplier as object), name: "Nordwind Großhandel GmbH" } };
    expect(nextEmptyRequired(specs, done, "supplier.name")).toBeNull();
  });
});
