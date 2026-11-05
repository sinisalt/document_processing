import { describe, expect, it } from "vitest";
import { attributeFields, attributeText, mergeAttributes, parseAttribute } from "./attributeSchema";

const schema = {
  properties: {
    erpVendorId: { type: "string", title: "ERP vendor id", pattern: "^V-\\d+$", maxLength: 20 },
    paymentDays: { type: "integer", title: "Payment days", minimum: 0 },
    costCentre: { type: "string", enum: ["4100", "4200"] },
    edi: { type: "boolean", title: "EDI" },
    nested: { type: "object" },
  },
  required: ["erpVendorId"],
};

describe("attribute schema (docs/11 §6.4)", () => {
  it("lists the typed properties in order with their constraints, and skips other types", () => {
    const fields = attributeFields(schema);
    expect(fields.map((f) => [f.key, f.kind, f.title, f.required])).toEqual([
      ["erpVendorId", "string", "ERP vendor id", true],
      ["paymentDays", "integer", "Payment days", false],
      ["costCentre", "string", "costCentre", false],
      ["edi", "boolean", "EDI", false],
    ]);
    expect(fields[0]).toMatchObject({ pattern: "^V-\\d+$", maxLength: 20 });
    expect(fields[2]!.options).toEqual(["4100", "4200"]);
    expect(attributeFields(null)).toEqual([]);
    expect(attributeFields({})).toEqual([]);
    expect(attributeFields([1])).toEqual([]);
  });

  it("reads the form's text by kind", () => {
    const [vendor, days, , edi] = attributeFields(schema);
    expect(parseAttribute(vendor!, " V-10042 ")).toEqual({ value: "V-10042" });
    expect(parseAttribute(vendor!, "")).toEqual({ value: null });
    expect(parseAttribute(days!, "30")).toEqual({ value: 30 });
    expect(parseAttribute(days!, "30,5")).toEqual({ error: "integer" });
    expect(parseAttribute(days!, "dreißig")).toEqual({ error: "number" });
    expect(parseAttribute(edi!, true)).toEqual({ value: true });
    expect(attributeText(30)).toBe("30");
    expect(attributeText(null)).toBe("");
  });

  it("keeps the keys the schema does not name and drops emptied fields", () => {
    const fields = attributeFields(schema);
    const merged = mergeAttributes({ legacy: "x", erpVendorId: "V-1", paymentDays: 14 }, fields, { erpVendorId: "V-2", paymentDays: null, costCentre: "4100", edi: false });
    expect(merged).toEqual({ legacy: "x", erpVendorId: "V-2", costCentre: "4100" });
    expect(mergeAttributes({ edi: true }, fields, { edi: false })).toEqual({ edi: false });
  });
});
