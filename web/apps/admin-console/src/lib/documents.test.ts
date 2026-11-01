import { describe, expect, it } from "vitest";
import { canonicalRows, documentLabel, documentSubtitle, formatCanonicalValue, pageCountLabel, shortId } from "./documents";

describe("pageCountLabel", () => {
  it("marks a full page with more behind it and otherwise counts exactly", () => {
    expect(pageCountLabel(200, "abc", 200)).toBe("200+");
    expect(pageCountLabel(12, null, 200)).toBe("12");
    expect(pageCountLabel(0, null, 200)).toBe("0");
    // A cursor without a full page cannot happen, but the label must still be honest about what it saw.
    expect(pageCountLabel(5, "abc", 200)).toBe("5");
  });
});

describe("formatCanonicalValue", () => {
  it("renders money, identifiers, parties, periods and dates", () => {
    expect(formatCanonicalValue({ amount: 12.5, currency: "EUR" }, "en")).toBe("€12.50");
    expect(formatCanonicalValue({ value: "INV-1", scheme: "Supplier" }, "en")).toBe("INV-1 [Supplier]");
    expect(formatCanonicalValue({ name: "METRO", vatId: "DE1" }, "en")).toBe("METRO (DE1)");
    expect(formatCanonicalValue({ start: "2026-01-01", end: "2026-01-31" }, "en")).toBe("Jan 1, 2026 – Jan 31, 2026");
    expect(formatCanonicalValue("2026-10-30", "en")).toBe("Oct 30, 2026");
    expect(formatCanonicalValue(null, "en")).toBe("—");
    expect(formatCanonicalValue(["a", "b"], "en")).toBe("a, b");
    expect(formatCanonicalValue({ verbatim: "2026/4471 ", normalized: "2026/4471" }, "en")).toBe("2026/4471");
    expect(formatCanonicalValue({ dueDate: "2026-11-28", paymentMeans: "BankTransfer", iban: null }, "en")).toBe("dueDate: Nov 28, 2026 · paymentMeans: BankTransfer");
    expect(formatCanonicalValue({ nested: { deep: true } }, "en")).toBe('{"nested":{"deep":true}}');
  });
});

describe("canonicalRows", () => {
  it("lists FieldValues with their status, counts lists and skips nothing", () => {
    const rows = canonicalRows(
      {
        invoiceNumber: { value: { value: "INV-7" }, status: "Extracted", confidence: 0.99 },
        totalGross: { value: { amount: 100, currency: "EUR" }, status: "Corrected" },
        lines: [{}, {}, {}],
        extra: "raw",
        servicePeriod: { status: "Missing", history: [], hasValue: false },
      },
      "en",
    );
    expect(rows.map((r) => r.path)).toEqual(["invoiceNumber", "totalGross", "lines", "extra", "servicePeriod"]);
    expect(rows[4]!.value).toBe("—");
    expect(rows[4]!.field?.status).toBe("Missing");
    expect(rows[0]!.value).toBe("INV-7");
    expect(rows[0]!.field?.status).toBe("Extracted");
    expect(rows[1]!.value).toBe("€100.00");
    expect(rows[2]!.count).toBe(3);
    expect(rows[3]!.field).toBeNull();
    expect(canonicalRows(null)).toEqual([]);
    expect(canonicalRows("x")).toEqual([]);
  });
});

describe("shortId and documentLabel", () => {
  // Two demo documents received in the same minute: UUIDv7 ids share their leading (time) digits.
  const first = "01a19012-f527-7d68-b5c6-2288526d662c";
  const second = "01a19012-11ab-766e-a116-e305e890879e";

  it("tells documents of the same minute apart by the random tail, not the time prefix", () => {
    expect(shortId(first)).toBe("526d662c");
    expect(shortId(second)).toBe("e890879e");
    expect(shortId(first)).not.toBe(shortId(second));
    expect(shortId("abc")).toBe("abc");
  });

  it("prefers the document number and falls back to the short id", () => {
    expect(documentLabel({ id: first, documentNumber: "RE-2026-55318" })).toBe("RE-2026-55318");
    expect(documentLabel({ id: first, documentNumber: null })).toBe("526d662c");
    expect(documentLabel({ id: first, documentNumber: "  " })).toBe("526d662c");
    expect(documentLabel({ id: first })).toBe("526d662c");
  });
});

describe("documentSubtitle", () => {
  it("names the tenant first and skips the parts a document does not have", () => {
    expect(documentSubtitle(["Pizzeria Napoli d.o.o.", "Unknown", null, "01a1905c"])).toBe("Pizzeria Napoli d.o.o. · Unknown · 01a1905c");
    expect(documentSubtitle([null, "Invoice", "Bio-Hof Müller", "01a1905c"])).toBe("Invoice · Bio-Hof Müller · 01a1905c");
    expect(documentSubtitle([undefined, "Invoice", "  ", "x"])).toBe("Invoice · x");
  });
});
