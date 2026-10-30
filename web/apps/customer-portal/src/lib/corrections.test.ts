import { describe, expect, it } from "vitest";
import { setByPath } from "@docproc/app-shell";
import { allSpecs, fieldDomId, formName, pathOfFormName, toFormValues, type CanonicalInvoice } from "./canonical";
import { diffCorrections, parseAmount, toCorrectionValue } from "./corrections";

const canonical: CanonicalInvoice = {
  kind: { value: "Invoice", status: "Extracted", confidence: 1 },
  invoiceNumber: { value: { verbatim: "RE-2026-10412", normalized: "RE-2026-10412" }, status: "Extracted", confidence: 1 },
  issueDate: { value: "2026-10-27", status: "Extracted" },
  currency: { value: "EUR", status: "Extracted" },
  supplier: { value: { name: "Fischhandel Müller GmbH", vatId: "DE812345678", address: { city: "Hamburg" } }, status: "Extracted", confidence: 0.9 },
  totalGross: { value: { amount: 437.99, currency: "EUR" }, status: "Extracted", confidence: 1 },
  notes: { value: null, status: "Missing" },
  orderReferences: [{ value: { verbatim: "PO-7781" }, status: "Extracted" }],
  paymentTerms: { value: { dueDate: "2026-11-10", paymentMeans: "BankTransfer" }, status: "Extracted" },
  lines: [
    {
      lineNumber: 1,
      description: { value: "Lachsfilet frisch", status: "Extracted" },
      quantity: { value: 12.5, status: "Extracted" },
      unitPrice: { value: { amount: 18.4, currency: "EUR" }, status: "Extracted" },
      lineNet: { value: { amount: 230, currency: "EUR" }, status: "Extracted" },
    },
  ],
};

describe("toFormValues", () => {
  it("flattens FieldValues into editable text, nested by canonical path", () => {
    const values = toFormValues(canonical, allSpecs(canonical)) as Record<string, unknown>;
    expect(values.invoiceNumber).toBe("RE-2026-10412");
    expect(values.totalGross).toBe("437.99");
    expect((values.supplier as Record<string, unknown>).name).toBe("Fischhandel Müller GmbH");
    expect(((values.supplier as Record<string, unknown>).address as Record<string, unknown>).city).toBe("Hamburg");
    expect(values.notes).toBe("");
    expect((values.orderReferences as unknown[])[0]).toBe("PO-7781");
    expect((values.lines as Array<Record<string, unknown>>)[0]?.quantity).toBe("12.5");
    expect((values.lines as Array<Record<string, unknown>>)[0]?.unitPrice).toBe("18.4");
  });
});

describe("diffCorrections", () => {
  const specs = allSpecs(canonical);
  const initial = toFormValues(canonical, specs);

  it("sends nothing when nothing changed", () => {
    expect(diffCorrections(specs, initial, structuredClone(initial))).toEqual([]);
  });

  it("sends only the changed paths with the API's value shapes", () => {
    let current = setByPath(initial, "invoiceNumber", "RE-2026-10413");
    current = setByPath(current, "totalGross", "438,00");
    current = setByPath(current, "supplier.address.city", "Bremen");
    current = setByPath(current, "lines[0].quantity", "13");
    current = setByPath(current, "notes", "  ");
    const corrections = diffCorrections(specs, initial, current, "typo");
    expect(corrections).toEqual([
      { path: "invoiceNumber", value: "RE-2026-10413", reason: "typo" },
      { path: "supplier.address.city", value: "Bremen", reason: "typo" },
      { path: "totalGross", value: 438, reason: "typo" },
      { path: "lines[0].quantity", value: 13, reason: "typo" },
    ]);
  });

  it("treats whitespace-only edits as unchanged and clears a field with null", () => {
    const current = setByPath(setByPath(initial, "currency", " EUR "), "issueDate", "");
    expect(diffCorrections(specs, initial, current)).toEqual([{ path: "issueDate", value: null, reason: null }]);
  });

  it("keeps a malformed number as text so the API's validation names the field", () => {
    const current = setByPath(initial, "lines[0].unitPrice", "abc");
    expect(diffCorrections(specs, initial, current)).toEqual([{ path: "lines[0].unitPrice", value: "abc", reason: null }]);
  });
});

describe("value helpers", () => {
  it("parses decimal commas and thousands separators", () => {
    expect(parseAmount("1.234,50")).toBe(1234.5);
    expect(parseAmount("1234,50")).toBe(1234.5);
    expect(parseAmount("1,234.50")).toBe(1234.5);
    expect(parseAmount("")).toBeNull();
    expect(parseAmount("x")).toBeNaN();
  });

  it("converts by kind", () => {
    expect(toCorrectionValue("money", "12")).toBe(12);
    expect(toCorrectionValue("number", "")).toBeNull();
    expect(toCorrectionValue("identifier", " A-1 ")).toBe("A-1");
    expect(toCorrectionValue("date", "2026-01-02")).toBe("2026-01-02");
  });

  it("maps canonical paths to form names and DOM ids and back", () => {
    expect(formName("lines[2].quantity")).toBe("lines.2.quantity");
    expect(pathOfFormName("lines.2.quantity")).toBe("lines[2].quantity");
    expect(formName("supplier.address.city")).toBe("supplier.address.city");
    expect(fieldDomId("lines[2].quantity")).toBe("field-lines_2__quantity");
  });
});
