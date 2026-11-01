import { describe, expect, it } from "vitest";
import { FieldPathError, getByPath, isWithin, joinPath, lastSegment, lineItemOf, parentPath, parsePath, setByPath } from "./fieldPath";

describe("parsePath / joinPath", () => {
  it("parses the canonical spellings", () => {
    expect(parsePath("invoiceNumber")).toEqual(["invoiceNumber"]);
    expect(parsePath("lines[2].quantity")).toEqual(["lines", 2, "quantity"]);
    expect(parsePath("supplier.vatId")).toEqual(["supplier", "vatId"]);
    expect(parsePath("lines[0]")).toEqual(["lines", 0]);
    expect(parsePath("a[1][2].b")).toEqual(["a", 1, 2, "b"]);
  });

  it("rejects malformed paths", () => {
    for (const bad of ["", " ", "[0]", "lines[", "lines[x]", "lines[-1]", "lines.", ".lines", "lines..a", "lines[0]a", "li ne", "lines[0.5]"]) {
      expect(() => parsePath(bad), bad).toThrow(FieldPathError);
    }
  });

  it("round-trips", () => {
    for (const path of ["invoiceNumber", "lines[2].quantity", "supplier.vatId", "a[1][2].b"]) {
      expect(joinPath(parsePath(path))).toBe(path);
    }
  });
});

describe("getByPath", () => {
  const doc = {
    invoiceNumber: { value: "INV-1", status: "Extracted" },
    supplier: { vatId: "DE123" },
    lines: [{ quantity: { value: 1 } }, { quantity: { value: 2 }, description: null }],
  };

  it("reads nested values", () => {
    expect(getByPath(doc, "invoiceNumber.value")).toBe("INV-1");
    expect(getByPath(doc, "lines[1].quantity.value")).toBe(2);
    expect(getByPath(doc, ["lines", 0, "quantity"])).toEqual({ value: 1 });
  });

  it("returns undefined for anything missing instead of throwing", () => {
    expect(getByPath(doc, "lines[5].quantity")).toBeUndefined();
    expect(getByPath(doc, "nope.deeper")).toBeUndefined();
    expect(getByPath(doc, "lines[1].description.value")).toBeUndefined();
    expect(getByPath(doc, "invoiceNumber[0]")).toBeUndefined();
    expect(getByPath(null, "a")).toBeUndefined();
  });
});

describe("setByPath", () => {
  it("writes immutably and shares untouched branches", () => {
    const doc = { lines: [{ quantity: 1 }, { quantity: 2 }], supplier: { name: "ACME" } };
    const next = setByPath(doc, "lines[1].quantity", 5);
    expect(next).toEqual({ lines: [{ quantity: 1 }, { quantity: 5 }], supplier: { name: "ACME" } });
    expect(doc.lines[1]!.quantity).toBe(2);
    expect(next.supplier).toBe(doc.supplier);
    expect(next.lines[0]).toBe(doc.lines[0]);
    expect(next.lines).not.toBe(doc.lines);
  });

  it("creates missing containers of the right kind", () => {
    expect(setByPath({}, "lines[1].quantity", 3)).toEqual({ lines: [undefined, { quantity: 3 }] });
    expect(setByPath(undefined, "supplier.vatId", "DE1")).toEqual({ supplier: { vatId: "DE1" } });
  });

  it("removes a key or element when the value is undefined", () => {
    expect(setByPath({ a: 1, b: 2 }, "b", undefined)).toEqual({ a: 1 });
    expect(setByPath({ lines: [1, 2, 3] }, "lines[1]", undefined)).toEqual({ lines: [1, 3] });
  });

  it("replaces the whole target for an empty segment list", () => {
    expect(setByPath({ a: 1 }, [], 42)).toBe(42);
  });
});

describe("path relations", () => {
  it("knows parents and last segments", () => {
    expect(parentPath("lines[2].quantity")).toBe("lines[2]");
    expect(parentPath("lines[2]")).toBe("lines");
    expect(parentPath("invoiceNumber")).toBeNull();
    expect(lastSegment("lines[2].quantity")).toBe("quantity");
    expect(lastSegment("lines[2]")).toBe(2);
  });

  it("recognises line-item paths", () => {
    expect(lineItemOf("lines[2].quantity")).toEqual({ index: 2, field: "quantity" });
    expect(lineItemOf("lines[0]")).toEqual({ index: 0, field: null });
    expect(lineItemOf("lines[0].unitPrice.amount")).toEqual({ index: 0, field: "unitPrice.amount" });
    expect(lineItemOf("invoiceNumber")).toBeNull();
    expect(lineItemOf("lines")).toBeNull();
  });

  it("tests containment by segment, not by string prefix", () => {
    expect(isWithin("lines[2].quantity", "lines[2]")).toBe(true);
    expect(isWithin("lines[2]", "lines[2]")).toBe(true);
    expect(isWithin("lines[20].quantity", "lines[2]")).toBe(false);
    expect(isWithin("lines", "lines[2]")).toBe(false);
  });
});
