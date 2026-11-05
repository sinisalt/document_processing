import { describe, expect, it } from "vitest";
import { gridsOf, unitsOfMeasure } from "./formSpec";
import { legacyFormSpec, legacyInvoiceFormSpec } from "./legacySpec";
import { toFormValues } from "./values";
import { allSpecs, legacyLayout } from "../test/fixtures";

const extracted = (value: unknown) => ({ value, status: "Extracted", confidence: 0.95, source: { method: "Llm", page: 1 } });
const line = (fields: Record<string, unknown>) => ({ lineNumber: 1, ...fields });

/** The grid columns the built-in invoice form shows for these lines, as the earlier `visibleLineColumns` did. */
function visibleColumns(lines: unknown[], flagged: string[] = []): string[] {
  const grid = gridsOf(legacyLayout({ lines }))[0]!;
  const flaggedColumns = new Set(flagged.map((path) => /^lines\[\d+\]\.(\w+)/.exec(path)?.[1]).filter(Boolean));
  return grid.columns
    .filter((column) => {
      if (column.alwaysShown || flaggedColumns.has(column.key)) return true;
      return lines.some((row) => {
        const field = (row as Record<string, { status?: string; value?: unknown }>)[column.key];
        return !!field && field.status !== "Missing" && field.value !== undefined && field.value !== null;
      });
    })
    .map((column) => column.key);
}

describe("the built-in invoice form", () => {
  it("is the form of invoices and credit notes only", () => {
    expect(legacyFormSpec("Invoice")).toBe(legacyInvoiceFormSpec);
    expect(legacyFormSpec("CreditNote")).toBe(legacyInvoiceFormSpec);
    expect(legacyFormSpec("PurchaseOrder")).toBeNull();
    expect(legacyFormSpec("price-list")).toBeNull();
  });

  it("keeps the sections and fields of the earlier review form, in order", () => {
    const layout = legacyLayout({ orderReferences: [extracted({ verbatim: "PO-1" })], lines: [line({})] });
    expect(layout.sections.map((section) => section.id)).toEqual(["document", "supplier", "customer", "totals", "payment", "notes", "lines"]);
    expect(layout.specs.map((spec) => spec.path)).toEqual([
      "kind",
      "invoiceNumber",
      "issueDate",
      "deliveryDate",
      "dueDate",
      "servicePeriod.from",
      "servicePeriod.to",
      "currency",
      "orderReferences[0]",
      "originalInvoiceReference",
      ...["name", "vatId", "taxId", "registrationNumber", "email", "phone", "iban", "bic", "address.line1", "address.line2", "address.postalCode", "address.city", "address.region", "address.countryCode"].map((m) => `supplier.${m}`),
      ...["name", "vatId", "taxId", "registrationNumber", "email", "phone", "iban", "bic", "address.line1", "address.line2", "address.postalCode", "address.city", "address.region", "address.countryCode"].map((m) => `customer.${m}`),
      "subtotalNet",
      "totalTax",
      "totalGross",
      "amountDue",
      "rounding",
      ...["dueDate", "discountPercent", "discountUntil", "paymentMeans", "paymentReference", "iban", "bic", "rawText"].map((m) => `paymentTerms.${m}`),
      "notes",
      ...["description", "quantity", "unit", "unitPrice", "lineNet", "taxRate", "supplierSku", "discountPercent", "discountAmount", "lineGross"].map((m) => `lines[0].${m}`),
    ]);
    const kinds = Object.fromEntries(layout.specs.map((spec) => [spec.path, spec.kind]));
    expect(kinds).toMatchObject({ kind: "enum", invoiceNumber: "identifier", issueDate: "date", "servicePeriod.from": "date", subtotalNet: "money", "paymentTerms.discountPercent": "number", "paymentTerms.paymentMeans": "enum", "paymentTerms.rawText": "text", notes: "text", "lines[0].unit": "enum" });
    // A member edits its field's envelope.
    expect(layout.specs.find((spec) => spec.path === "supplier.address.city")).toMatchObject({ fieldPath: "supplier" });
    expect(layout.structural).toBe(false);
  });

  it("offers the API's UnitOfMeasure names for a line's unit", () => {
    expect(allSpecs({ lines: [line({})] }).find((spec) => spec.path === "lines[0].unit")).toMatchObject({ kind: "enum", options: unitsOfMeasure });
  });

  it("hides the original invoice reference on a plain invoice and shows it on a credit note", () => {
    const spec = allSpecs({}).find((s) => s.path === "originalInvoiceReference")!;
    expect(spec.visibleWhen).toEqual({ path: "kind", equals: "CreditNote" });
  });
});

describe("line columns", () => {
  it("puts what the line check is about first and leaves out optional columns no line uses", () => {
    const columns = visibleColumns([line({ description: extracted("Karotten"), quantity: extracted(10), unitPrice: extracted({ amount: 1.9, currency: "EUR" }), lineNet: extracted({ amount: 19, currency: "EUR" }) })]);
    expect(columns).toEqual(["description", "quantity", "unit", "unitPrice", "lineNet", "taxRate"]);
    expect(gridsOf(legacyLayout({ lines: [] }))[0]!.arithmetic).toBe(true);
  });

  it("shows an optional column once a line carries a value in it or a flag names it", () => {
    const withSku = visibleColumns([line({ supplierSku: extracted("A-17") }), line({})]);
    expect(withSku).toContain("supplierSku");
    expect(withSku).not.toContain("lineGross");
    expect(visibleColumns([line({})], ["lines[0].discountAmount"])).toContain("discountAmount");
  });

  it("takes a Missing field's serialised default (a discount of 0) for no value", () => {
    const missing = { value: 0, status: "Missing", history: [], hasValue: false };
    const invoice = { lines: [line({ discountPercent: missing })] };
    expect(visibleColumns(invoice.lines)).not.toContain("discountPercent");
    expect(toFormValues(invoice, allSpecs(invoice))).toMatchObject({ lines: [{ discountPercent: "" }] });
  });

  it("numbers the rows by their line number and shows the printed unit", () => {
    const grid = gridsOf(legacyLayout({ lines: [line({})] }))[0]!;
    expect(grid).toMatchObject({ rowNumberKey: "lineNumber", printedUnitKey: "unitRaw", rowCount: 1 });
  });
});
