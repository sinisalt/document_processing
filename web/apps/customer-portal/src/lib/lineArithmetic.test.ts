import { describe, expect, it } from "vitest";
import { checkLineArithmetic } from "./lineArithmetic";

describe("checkLineArithmetic", () => {
  it("passes when quantity × unit price equals the line net within a cent", () => {
    expect(checkLineArithmetic({ quantity: 12.5, unitPrice: 18.4, lineNet: 230 })).toMatchObject({ status: "ok", expected: 230, difference: 0 });
    expect(checkLineArithmetic({ quantity: 3, unitPrice: 1.333, lineNet: 4 })).toMatchObject({ status: "ok" });
  });

  it("flags a mismatch beyond the tolerance and reports the difference", () => {
    const result = checkLineArithmetic({ quantity: 10, unitPrice: 2.5, lineNet: 27.5 });
    expect(result.status).toBe("mismatch");
    expect(result.expected).toBe(25);
    expect(result.difference).toBe(2.5);
  });

  it("applies a discount amount before a discount percent", () => {
    expect(checkLineArithmetic({ quantity: 10, unitPrice: 10, discountAmount: -5, lineNet: 95 }).status).toBe("ok");
    expect(checkLineArithmetic({ quantity: 10, unitPrice: 10, discountPercent: 10, lineNet: 90 }).status).toBe("ok");
    expect(checkLineArithmetic({ quantity: 10, unitPrice: 10, discountPercent: 10, discountAmount: 5, lineNet: 90 }).status).toBe("mismatch");
  });

  it("accepts a sign carried elsewhere (credit notes)", () => {
    expect(checkLineArithmetic({ quantity: 2, unitPrice: 5, lineNet: -10 }).status).toBe("ok");
  });

  it("is incomplete, not wrong, while a factor is missing, and accepts the API's string numbers", () => {
    expect(checkLineArithmetic({ quantity: null, unitPrice: 5, lineNet: 10 })).toMatchObject({ status: "incomplete", expected: null });
    expect(checkLineArithmetic({ quantity: "2", unitPrice: "5", lineNet: "10" }).status).toBe("ok");
    expect(checkLineArithmetic({ quantity: "", unitPrice: "5", lineNet: "10" }).status).toBe("incomplete");
  });

  it("honours a custom tolerance", () => {
    expect(checkLineArithmetic({ quantity: 1, unitPrice: 1, lineNet: 1.04 }, 0.05).status).toBe("ok");
    expect(checkLineArithmetic({ quantity: 1, unitPrice: 1, lineNet: 1.04 }).status).toBe("mismatch");
  });
});
