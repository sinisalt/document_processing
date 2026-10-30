import { describe, expect, it } from "vitest";
import { confidenceVariant, hasValue, isFieldValue, isLowConfidence, type FieldValue } from "./fieldValue";

const field = (partial: Partial<FieldValue>): FieldValue => ({ value: 1, status: "Extracted", ...partial });

describe("isLowConfidence", () => {
  it("compares a known confidence against the threshold", () => {
    expect(isLowConfidence(field({ confidence: 0.79 }))).toBe(true);
    expect(isLowConfidence(field({ confidence: 0.8 }))).toBe(false);
    expect(isLowConfidence(field({ confidence: 0.9 }), 0.95)).toBe(true);
  });

  it("treats unsure statuses as low and settled ones as fine", () => {
    expect(isLowConfidence(field({ status: "Conflict", confidence: 0 }))).toBe(true);
    expect(isLowConfidence(field({ status: "Unreadable", confidence: 0 }))).toBe(true);
    expect(isLowConfidence(field({ status: "Corrected", confidence: 1 }))).toBe(false);
    expect(isLowConfidence(field({ status: "Missing", value: null }))).toBe(false);
    expect(isLowConfidence(field({ confidence: null }))).toBe(false);
    expect(isLowConfidence(null)).toBe(false);
  });
});

describe("hasValue and isFieldValue", () => {
  it("mirrors FieldValue.HasValue", () => {
    expect(hasValue(field({ value: 0 }))).toBe(true);
    expect(hasValue(field({ value: null }))).toBe(false);
    expect(hasValue(field({ value: 1, status: "Missing" }))).toBe(false);
    expect(hasValue(field({ value: 1, status: "Unreadable" }))).toBe(false);
    expect(hasValue(undefined)).toBe(false);
  });

  it("recognises the serialised shape", () => {
    expect(isFieldValue({ value: "x", status: "Extracted" })).toBe(true);
    expect(isFieldValue({ value: null, status: "Missing", confidence: null })).toBe(true);
    expect(isFieldValue({ status: "Extracted" })).toBe(false);
    expect(isFieldValue({ value: 1, status: "Great" })).toBe(false);
    expect(isFieldValue("x")).toBe(false);
  });
});

describe("confidenceVariant", () => {
  it("bands confidences", () => {
    expect(confidenceVariant(0.99)).toBe("secondary");
    expect(confidenceVariant(0.9)).toBe("outline");
    expect(confidenceVariant(0.5)).toBe("destructive");
    expect(confidenceVariant(null)).toBe("outline");
    expect(confidenceVariant(0.9, 0.95)).toBe("destructive");
  });
});
