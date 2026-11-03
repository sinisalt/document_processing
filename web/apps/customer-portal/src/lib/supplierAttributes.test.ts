import { describe, expect, it } from "vitest";
import { attributesText, parseAttributes } from "./supplierAttributes";

describe("supplier attributes in the supplier sheet", () => {
  it("writes one key = value per line and reads it back", () => {
    const text = attributesText({ erpVendorId: "V-10042", paymentDays: 30, blocked: false, note: null });
    expect(text).toBe("erpVendorId = V-10042\npaymentDays = 30\nblocked = false\nnote = ");
    expect(parseAttributes(text, { erpVendorId: "V-10042", paymentDays: 30, blocked: false, note: null })).toEqual({ attributes: { erpVendorId: "V-10042", paymentDays: 30, blocked: false, note: null } });
  });

  it("keeps a new or changed value as text and skips blank lines", () => {
    expect(parseAttributes("erpVendorId: V-10042\n\npaymentDays = 45", { paymentDays: 30 })).toEqual({ attributes: { erpVendorId: "V-10042", paymentDays: "45" } });
    expect(parseAttributes("", { a: "b" })).toEqual({ attributes: {} });
  });

  it("names the first line that is not an attribute", () => {
    expect(parseAttributes("erpVendorId = V-1\njust text", null)).toEqual({ invalidLine: 2 });
    expect(parseAttributes("1abc = x", null)).toEqual({ invalidLine: 1 });
  });
});
