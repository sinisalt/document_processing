import { describe, expect, it } from "vitest";
import { addField, childrenOf, fieldAt, fieldIssues, isDefinition, moveField, newFieldId, newFieldKey, newTypeDefinition, normalizeKind, removeField, updateField, type Definition } from "./model";

const definition: Definition = {
  key: "CreditNote",
  version: 2,
  name: { en: "Credit note" },
  rules: [{ id: "r1", kind: "invoice.totals" }],
  fields: [
    { id: "fld_inv_no", key: "invoiceNumber", kind: "identifier", indexed: true },
    {
      id: "fld_lines",
      key: "lines",
      kind: "array",
      item: { fields: [{ id: "fld_l_desc", key: "description", kind: "text" }, { id: "fld_l_qty", key: "quantity", kind: "decimal" }] },
    },
  ],
};

describe("definition editing", () => {
  it("finds fields at the top and inside a repeating group", () => {
    expect(fieldAt(definition, [0])?.key).toBe("invoiceNumber");
    expect(fieldAt(definition, [1, 1])?.key).toBe("quantity");
    expect(fieldAt(definition, [5])).toBeNull();
    expect(childrenOf(definition.fields[1]!)?.map((f) => f.key)).toEqual(["description", "quantity"]);
  });

  it("changes a field without touching the rest, and keeps what the editor does not edit", () => {
    const next = updateField(definition, [1, 0], { label: { en: "Description", de: "Bezeichnung" }, required: true });
    expect(fieldAt(next, [1, 0])).toEqual({ id: "fld_l_desc", key: "description", kind: "text", label: { en: "Description", de: "Bezeichnung" }, required: true });
    expect(next.rules).toBe(definition.rules);
    expect(next.fields[0]).toBe(definition.fields[0]);
    // The input is not changed.
    expect(fieldAt(definition, [1, 0])?.label).toBeUndefined();
  });

  it("adds a custom field with a new id, moves and removes fields", () => {
    const added = addField(definition, null, { id: newFieldId(definition), key: newFieldKey(definition.fields, "supplierCustomerRef"), kind: "identifier", indexed: true, label: { en: "Our customer no. at supplier" } });
    expect(added.location).toEqual([2]);
    expect(fieldAt(added.definition, [2])).toMatchObject({ id: "fld_c1", key: "supplierCustomerRef" });

    const row = addField(added.definition, [1], { id: newFieldId(added.definition), key: newFieldKey(childrenOf(added.definition.fields[1]!)!), kind: "string" });
    expect(row.location).toEqual([1, 2]);
    expect(fieldAt(row.definition, [1, 2])).toMatchObject({ id: "fld_c2", key: "newField" });

    const up = moveField(row.definition, [2], -1);
    expect(up.location).toEqual([1]);
    expect(up.definition.fields.map((f) => f.key)).toEqual(["invoiceNumber", "supplierCustomerRef", "lines"]);
    expect(moveField(up.definition, [0], -1).location).toEqual([0]);

    expect(removeField(up.definition, [1]).fields.map((f) => f.key)).toEqual(["invoiceNumber", "lines"]);
    expect(newFieldKey([{ id: "a", key: "newField", kind: "string" }])).toBe("newField2");
  });

  it("keeps the attributes of a field consistent with its kind", () => {
    expect(normalizeKind({ id: "f", key: "k", kind: "enum" }, "string")).toEqual({ id: "f", key: "k", kind: "enum", options: [] });
    expect(normalizeKind({ id: "f", key: "k", kind: "money" }, "string")).toEqual({ id: "f", key: "k", kind: "money", currencyFrom: "currency" });
    expect(normalizeKind({ id: "f", key: "k", kind: "array", indexed: true }, "string")).toEqual({ id: "f", key: "k", kind: "array", item: { fields: [] } });
    expect(normalizeKind({ id: "f", key: "k", kind: "string", options: [{ code: "A" }], currencyFrom: "currency" }, "enum")).toEqual({ id: "f", key: "k", kind: "string" });
    expect(normalizeKind({ id: "f", key: "k", kind: "unit" }, "string")).toEqual({ id: "f", key: "k", kind: "unit", codeList: "uom" });
    expect(updateField(definition, [0], { kind: "enum" }).fields[0]).toMatchObject({ kind: "enum", options: [] });
  });

  it("names what would make a save fail", () => {
    const broken = {
      ...definition,
      fields: [...definition.fields, { id: "fld_x", key: "Bad Key", kind: "string" }, { id: "fld_y", key: "invoiceNumber", kind: "string" }, { id: "fld_z", key: "reason", kind: "enum", options: [] }, { id: "fld_p", key: "partner", kind: "party", indexed: true }],
    };
    expect(fieldIssues(broken)).toEqual([
      { location: [2], code: "keyInvalid" },
      { location: [3], code: "keyDuplicate" },
      { location: [4], code: "optionsMissing" },
      { location: [5], code: "indexedKind" },
    ]);
    expect(fieldIssues(definition)).toEqual([]);
  });

  it("starts a type of the tenant's own with a number and a date", () => {
    const created = newTypeDefinition("return-slip", { en: "Return slip" });
    expect(isDefinition(created)).toBe(true);
    expect(created.fields.map((f) => [f.key, f.kind])).toEqual([
      ["documentNumber", "identifier"],
      ["documentDate", "date"],
    ]);
    expect(fieldIssues(created)).toEqual([]);
    expect(isDefinition({ key: "x" })).toBe(false);
  });
});
