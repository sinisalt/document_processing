import { describe, expect, it } from "vitest";
import type { DocumentTypeDetail } from "@docproc/review";
import { fieldFilterQuery, indexedFieldOptions, statusFilterOf, typeLabel, typeOptions } from "./inbox";

const type = (key: string, name: Record<string, string>, indexedFields: DocumentTypeDetail["indexedFields"]) => ({ key, name, indexedFields }) as unknown as DocumentTypeDetail;

const types = [
  type("Invoice", { en: "Invoice" }, [{ path: "invoiceNumber", kind: "identifier", label: { en: "Invoice no.", de: "Rechnungsnummer" } }]),
  type("CreditNote", { en: "Credit note" }, [
    { path: "invoiceNumber", kind: "identifier", label: { en: "Credit note no." } },
    { path: "supplierCustomerRef", kind: "identifier", label: { en: "Our customer no. at supplier", de: "Unsere Kundennummer beim Lieferanten" } },
  ]),
  type("price-list", { en: "Price list", de: "Preisliste" }, [{ path: "validFrom", kind: "date", label: { en: "Valid from" } }]),
  type("acme-return", { en: "Acme return slip" }, []),
];

describe("inbox type filter", () => {
  it("lists the legacy types first, then the tenant's own types by name", () => {
    expect(typeOptions(types, "en").map((o) => o.key)).toEqual(["Invoice", "CreditNote", "PurchaseOrder", "DeliveryNote", "Statement", "Reminder", "Other", "Unknown", "acme-return", "price-list"]);
    expect(typeOptions(types, "de").find((o) => o.key === "price-list")?.name).toBe("Preisliste");
    // Without the catalogue (a platform account, a failed request) the legacy types remain.
    expect(typeOptions(undefined, "en")).toHaveLength(8);
  });

  it("names a catalogue type in the Type column by its catalogue name, and leaves a legacy type to its translation", () => {
    expect(typeLabel("price-list", typeOptions(types, "en"))).toBe("Price list");
    expect(typeLabel("price-list", typeOptions(types, "de"))).toBe("Preisliste");
    expect(typeLabel("Invoice", typeOptions(types, "en"))).toBeUndefined();
    expect(typeLabel("price-list", typeOptions(undefined, "en"))).toBeUndefined();
  });
});

describe("inbox field filter", () => {
  it("offers the indexed fields of the selected type, or of all types once each", () => {
    expect(indexedFieldOptions(types, "CreditNote", "en").map((o) => [o.path, o.name])).toEqual([
      ["invoiceNumber", "Credit note no."],
      ["supplierCustomerRef", "Our customer no. at supplier"],
    ]);
    const all = indexedFieldOptions(types, undefined, "de");
    expect(all.map((o) => o.path)).toEqual(["invoiceNumber", "supplierCustomerRef", "validFrom"]);
    expect(all[0]).toMatchObject({ name: "Rechnungsnummer", types: ["Invoice", "CreditNote"] });
    expect(indexedFieldOptions(types, "PurchaseOrder", "en")).toEqual([]);
  });

  it("becomes the field.<path> parameter of the list", () => {
    expect(fieldFilterQuery("supplierCustomerRef", " 0815 ")).toEqual({ "field.supplierCustomerRef": "0815" });
    expect(fieldFilterQuery("lines[*].supplierSku", "A-1")).toEqual({ "field.lines[*].supplierSku": "A-1" });
    expect(fieldFilterQuery("supplierCustomerRef", "  ")).toEqual({});
    expect(fieldFilterQuery(undefined, "x")).toEqual({});
  });

  it("keeps the status tabs", () => {
    expect(statusFilterOf("needsReview")).toBe("NeedsReview");
    expect(statusFilterOf("all")).toBeUndefined();
  });
});
