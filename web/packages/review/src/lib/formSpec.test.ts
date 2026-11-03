import { describe, expect, it } from "vitest";
import { i18n } from "@docproc/app-shell";
import { gridsOf, isFormSpec, resolvePath, specsFromDefinition, uneceUnits, unitsOfMeasure, type FormNode } from "./formSpec";
import { labelOf } from "./labels";
import { toFormValues } from "./values";
import { extracted, missing, money } from "../test/fixtures";
import { forkWith, templates } from "../test/templates";

const t = i18n.getFixedT("en", "review");
const tDe = i18n.getFixedT("de", "review");

/** A delivery note as Extract stores it for a definition-driven type (docs/11 §5.6): every field Missing, no rows. */
const emptyDeliveryNote = {
  deliveryNoteNumber: missing(),
  deliveryDate: missing(),
  supplier: missing(),
  recipient: missing(),
  orderReferences: [],
  carrier: missing(),
  signedBy: missing(),
  temperatureReadings: [],
  notes: missing(),
  lines: [],
};

function nodeTypes(nodes: FormNode[]): string[] {
  return nodes.map((node) => `${node.type}:${node.type === "field" ? node.spec.path : node.path}`);
}

describe("specsFromDefinition", () => {
  it("recognises a compiled form spec", () => {
    expect(isFormSpec(templates.deliveryNote)).toBe(true);
    expect(isFormSpec({ fields: [] })).toBe(false);
    expect(isFormSpec(null)).toBe(false);
  });

  it("lays a delivery note out by the sections of its definition", () => {
    const layout = specsFromDefinition(templates.deliveryNote, emptyDeliveryNote, { schemaVersion: 2 });
    expect(layout.structural).toBe(true);
    expect(layout.sections.map((s) => s.id)).toEqual(["document", "parties", "references", "lines"]);
    // Read-only members without provenance (temperature readings) are not part of the form.
    expect(nodeTypes(layout.sections[0]!.nodes)).toEqual(["field:deliveryNoteNumber", "field:deliveryDate", "field:carrier", "field:signedBy", "field:notes"]);
    expect(nodeTypes(layout.sections[1]!.nodes)).toEqual(["composite:supplier", "composite:recipient"]);
    expect(nodeTypes(layout.sections[2]!.nodes)).toEqual(["list:orderReferences"]);
    expect(nodeTypes(layout.sections[3]!.nodes)).toEqual(["grid:lines"]);
    expect(layout.specs.find((s) => s.path === "recipient.address.city")).toMatchObject({ kind: "string", fieldPath: "recipient", member: { of: "party", path: "address.city" } });
    expect(layout.specs.find((s) => s.path === "deliveryNoteNumber")).toMatchObject({ kind: "identifier", label: { en: "Delivery note no.", de: "Lieferscheinnummer" } });
  });

  it("makes one spec per stored row and offers the UN/ECE unit codes of a version 2 tree", () => {
    const tree = { ...emptyDeliveryNote, lines: [{ description: extracted("Kabeljau"), quantityDelivered: extracted(4), unit: missing() }, { description: missing() }] };
    const layout = specsFromDefinition(templates.deliveryNote, tree, { schemaVersion: 2 });
    const grid = gridsOf(layout)[0]!;
    expect(grid.rowCount).toBe(2);
    expect(grid.arithmetic).toBe(false);
    expect(grid.columns.map((c) => c.key)).toEqual(["supplierSku", "description", "quantityDelivered", "unit", "quantityOrdered", "lotNumber", "bestBefore", "backorderQuantity"]);
    expect(grid.columns.find((c) => c.key === "description")).toMatchObject({ wide: true, kind: "text" });
    expect(grid.columns.find((c) => c.key === "unit")!.options).toEqual(Object.keys(uneceUnits));
    expect(layout.specs.filter((s) => s.path.startsWith("lines[1].")).map((s) => s.path)).toContain("lines[1].quantityDelivered");
    expect(toFormValues(tree, layout.specs)).toMatchObject({ lines: [{ description: "Kabeljau", quantityDelivered: "4", unit: "" }, { description: "" }] });
  });

  it("keeps the unit names in a version 2 tree whose rows hold names (a fork of the invoice family)", () => {
    const tree = { lines: [{ unit: extracted("Kilogram") }] };
    const grid = gridsOf(specsFromDefinition(templates.invoice, tree, { schemaVersion: 2 }))[0]!;
    expect(grid.columns.find((c) => c.key === "unit")!.options).toEqual(unitsOfMeasure);
    expect(grid.arithmetic).toBe(true);
    expect(gridsOf(specsFromDefinition(templates.invoice, { lines: [] }, { schemaVersion: 1 }))[0]!.columns.find((c) => c.key === "unit")!.options).toEqual(unitsOfMeasure);
  });

  it("offers the unit names for an invoice-family tree of version 2 even when no row holds a unit yet", () => {
    const tree = { lines: [{ unit: { status: "Missing", history: [] } }] };
    const unit = (invoiceFamily: boolean) => gridsOf(specsFromDefinition(templates.invoice, tree, { schemaVersion: 2, invoiceFamily }))[0]!.columns.find((c) => c.key === "unit")!.options;
    expect(unit(true)).toEqual(unitsOfMeasure);
    expect(unit(false)).toEqual(Object.keys(uneceUnits));
    const layout = specsFromDefinition(templates.invoice, tree, { schemaVersion: 2, invoiceFamily: true });
    expect(layout.structural).toBe(true);
  });

  it("shows the first columns of a repeating group without required columns until values exist", () => {
    const grid = gridsOf(specsFromDefinition(templates.deliveryNote, emptyDeliveryNote, { schemaVersion: 2 }))[0]!;
    expect(grid.columns.filter((c) => c.alwaysShown).map((c) => c.key)).toEqual(["supplierSku", "description", "quantityDelivered", "unit", "quantityOrdered", "lotNumber"]);
    // The price list requires its SKU, description and price: those are the columns, the rest come with values.
    const prices = gridsOf(specsFromDefinition(templates.priceList, { lines: [] }, { schemaVersion: 2 }))[0]!;
    expect(prices.columns.filter((c) => c.alwaysShown).map((c) => c.key)).toEqual(["supplierSku", "description", "price"]);
    expect(prices.nested.map((n) => n.key)).toEqual(["tiers"]);
  });

  it("lists the entries of a list field and the keys of an attribute bag", () => {
    const tree = { ext: { storeNo: extracted("0815"), promo: extracted(true) }, lines: [] };
    const layout = specsFromDefinition(templates.priceList, tree, { schemaVersion: 2 });
    const bag = layout.sections.flatMap((s) => s.nodes).find((n) => n.type === "bag")!;
    expect(bag.type === "bag" && bag.entries.map((e) => [e.path, e.kind])).toEqual([
      ["ext.storeNo", "string"],
      ["ext.promo", "boolean"],
    ]);
    const po = specsFromDefinition(templates.deliveryNote, { ...emptyDeliveryNote, orderReferences: [extracted({ verbatim: "PO-1" }), extracted({ verbatim: "PO-2" })] }, { schemaVersion: 2 });
    expect(po.specs.filter((s) => s.path.startsWith("orderReferences")).map((s) => s.path)).toEqual(["orderReferences[0]", "orderReferences[1]"]);
  });

  it("adds a custom field of a fork with its own label, kind and section", () => {
    const fork = forkWith(templates.creditNote, "CreditNote", {
      path: "supplierCustomerRef",
      key: "supplierCustomerRef",
      kind: "identifier",
      section: "document",
      indexed: true,
      label: { en: "Our customer no. at supplier", de: "Unsere Kundennummer beim Lieferanten" },
    });
    const layout = specsFromDefinition(fork, { supplierCustomerRef: extracted({ verbatim: "0815" }), lines: [] }, { schemaVersion: 2 });
    const document = layout.sections.find((s) => s.id === "document")!;
    expect(nodeTypes(document.nodes)).toContain("field:supplierCustomerRef");
    expect(toFormValues({ supplierCustomerRef: extracted({ verbatim: "0815" }) }, layout.specs)).toMatchObject({ supplierCustomerRef: "0815" });
    expect(labelOf(t, "en", layout, "supplierCustomerRef")).toBe("Our customer no. at supplier");
    expect(labelOf(tDe, "de", layout, "supplierCustomerRef")).toBe("Unsere Kundennummer beim Lieferanten");
  });

  it("puts a field of an undeclared section into the other fields, and leaves out a deprecated field without a value", () => {
    const spec = forkWith(templates.purchaseOrder, "po", { path: "costCentre", key: "costCentre", kind: "string" }, { path: "old", key: "old", kind: "string", section: "document", deprecated: true });
    const layout = specsFromDefinition(spec, {}, { schemaVersion: 2 });
    expect(layout.sections.at(-1)).toMatchObject({ id: "__other" });
    expect(layout.specs.map((s) => s.path)).toContain("costCentre");
    expect(layout.specs.map((s) => s.path)).not.toContain("old");
    expect(specsFromDefinition(spec, { old: extracted("x") }, { schemaVersion: 2 }).specs.map((s) => s.path)).toContain("old");
  });

  it("takes enum options and their labels from the definition", () => {
    const layout = specsFromDefinition(templates.priceList, { lines: [] }, { schemaVersion: 2 });
    expect(layout.specs.find((s) => s.path === "listScope")).toMatchObject({ kind: "enum", options: ["Full", "Changes"], optionLabels: { Full: { en: "Full list", de: "Gesamtliste" } } });
    expect(layout.specs.find((s) => s.path === "declaredLineCount")).toMatchObject({ kind: "number", integer: true });
    expect(layout.specs.find((s) => s.path === "validFrom")).toMatchObject({ kind: "date", required: true });
  });
});

describe("labels", () => {
  const layout = specsFromDefinition(templates.deliveryNote, { ...emptyDeliveryNote, lines: [{}, {}, {}] }, { schemaVersion: 2 });

  it("reads definition labels, with the UI language for composite members", () => {
    expect(labelOf(t, "en", layout, "deliveryNoteNumber")).toBe("Delivery note no.");
    expect(labelOf(tDe, "de", layout, "deliveryNoteNumber")).toBe("Lieferscheinnummer");
    expect(labelOf(t, "en", layout, "recipient")).toBe("Recipient");
    expect(labelOf(t, "en", layout, "recipient.vatId")).toBe("VAT ID");
    expect(labelOf(tDe, "de", layout, "recipient.address.city")).toBe("Ort");
  });

  it("names the row of a repeating group and the entry of a list", () => {
    expect(labelOf(t, "en", layout, "lines[2].quantityDelivered")).toBe("Quantity delivered");
    expect(labelOf(t, "en", layout, "lines[2].quantityDelivered", true)).toBe("Line 3 · Quantity delivered");
    expect(labelOf(t, "en", layout, "lines[2]")).toBe("Line 3");
    expect(labelOf(t, "en", layout, "orderReferences[1]")).toBe("Order nos. 2");
    const prices = specsFromDefinition(templates.priceList, { lines: [{}] }, { schemaVersion: 2 });
    expect(labelOf(t, "en", prices, "lines[0].price", true)).toBe("Line 1 · Price");
    expect(labelOf(t, "en", prices, "lines[0].tiers[1].price", true)).toBe("Tier prices 2 · Price");
    expect(labelOf(t, "en", prices, "ext.storeNo")).toBe("storeNo");
  });

  it("falls back to the built-in labels and then to the key in words", () => {
    expect(labelOf(t, "en", null, "invoiceNumber")).toBe("Invoice number");
    expect(labelOf(t, "en", null, "lines[0].quantity", true)).toBe("Line 1 · Qty");
    expect(labelOf(t, "en", null, "supplierCustomerRef")).toBe("Supplier customer ref");
  });

  it("resolves concrete paths to definition fields", () => {
    const resolved = resolvePath(layout.fields, "lines[2].quantityDelivered");
    expect(resolved.field?.path).toBe("lines[*].quantityDelivered");
    expect(resolved.row?.index).toBe(2);
    expect(resolvePath(layout.fields, "supplier.address.city")).toMatchObject({ member: "address.city", field: { key: "supplier" } });
  });
});

describe("money in a definition", () => {
  it("edits the amount and reads the currency of the stored value", () => {
    const layout = specsFromDefinition(templates.purchaseOrder, { totalNet: extracted(money(12.5)), currency: extracted("CHF") }, { schemaVersion: 2 });
    expect(toFormValues({ totalNet: extracted(money(12.5)) }, layout.specs, "de")).toMatchObject({ totalNet: "12,50" });
    expect(layout.specs.find((s) => s.path === "totalNet")).toMatchObject({ kind: "money", currencyPath: "currency" });
  });
});
