import { describe, expect, it } from "vitest";
import { canonicalRows, formatCanonicalValue, pageCountLabel } from "./documents";

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
    expect(formatCanonicalValue({ weird: true }, "en")).toBe('{"weird":true}');
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
      },
      "en",
    );
    expect(rows.map((r) => r.path)).toEqual(["invoiceNumber", "totalGross", "lines", "extra"]);
    expect(rows[0]!.value).toBe("INV-7");
    expect(rows[0]!.field?.status).toBe("Extracted");
    expect(rows[1]!.value).toBe("€100.00");
    expect(rows[2]!.count).toBe(3);
    expect(rows[3]!.field).toBeNull();
    expect(canonicalRows(null)).toEqual([]);
    expect(canonicalRows("x")).toEqual([]);
  });
});
