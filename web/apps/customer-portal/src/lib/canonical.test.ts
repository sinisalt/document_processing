import { describe, expect, it } from "vitest";
import { headerSections, isFieldApplicable, lineSpecs, toFormValues, visibleLineColumns, type CanonicalInvoice, type CanonicalLine } from "./canonical";

const extracted = (value: unknown) => ({ value, status: "Extracted", confidence: 0.95, source: { method: "Llm", page: 1 } });

const line = (fields: Record<string, unknown>) => ({ lineNumber: 1, ...fields }) as unknown as CanonicalLine;

describe("visibleLineColumns", () => {
  it("puts what the line check is about first and leaves out optional columns no line uses", () => {
    const columns = visibleLineColumns([line({ description: extracted("Karotten"), quantity: extracted(10), unitPrice: extracted({ amount: 1.9, currency: "EUR" }), lineNet: extracted({ amount: 19, currency: "EUR" }) })]);
    expect(columns.map((c) => c.name)).toEqual(["description", "quantity", "unit", "unitPrice", "lineNet", "taxRate"]);
  });

  it("shows an optional column once a line carries a value in it or a flag names it", () => {
    const withSku = visibleLineColumns([line({ supplierSku: extracted("A-17") }), line({})]);
    expect(withSku.map((c) => c.name)).toContain("supplierSku");
    expect(withSku.map((c) => c.name)).not.toContain("lineGross");

    const flagged = visibleLineColumns([line({})], ["lines[0].discountAmount"]);
    expect(flagged.map((c) => c.name)).toContain("discountAmount");
  });

  it("takes a Missing field's serialised default (a discount of 0) for no value", () => {
    const missing = { value: 0, status: "Missing", history: [], hasValue: false };
    expect(visibleLineColumns([line({ discountPercent: missing, lineGross: { ...missing, value: { amount: 0 } } })]).map((c) => c.name)).not.toContain("discountPercent");
    const invoice = { lines: [line({ discountPercent: missing })] } as unknown as CanonicalInvoice;
    expect(toFormValues(invoice, lineSpecs(invoice))).toMatchObject({ lines: [{ discountPercent: "" }] });
  });
});

describe("isFieldApplicable", () => {
  const spec = headerSections(null)
    .flatMap((section) => section.fields)
    .find((s) => s.path === "originalInvoiceReference")!;
  const invoice = { kind: extracted("Invoice") } as unknown as CanonicalInvoice;

  it("hides the original invoice reference on a plain invoice and shows it on a credit note", () => {
    expect(isFieldApplicable(spec, "Invoice", invoice)).toBe(false);
    expect(isFieldApplicable(spec, "CreditNote", invoice)).toBe(true);
  });

  it("keeps it on an invoice that carries one, and leaves other fields alone", () => {
    const withReference = { ...invoice, originalInvoiceReference: extracted({ verbatim: "RE-1", normalized: "RE1" }) } as unknown as CanonicalInvoice;
    expect(isFieldApplicable(spec, "Invoice", withReference)).toBe(true);
    expect(isFieldApplicable({ path: "dueDate", kind: "date", fieldPath: "dueDate" }, "Invoice", invoice)).toBe(true);
  });
});
