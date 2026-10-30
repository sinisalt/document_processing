import { describe, expect, it } from "vitest";
import { detailRows, detailsJson, eventKind, eventStage, summarizeEvent } from "./timeline";

describe("eventKind", () => {
  it("strips the document. prefix and leaves unknown types alone", () => {
    expect(eventKind("document.stage_failed")).toBe("stage_failed");
    expect(eventKind("custom")).toBe("custom");
  });
});

describe("eventStage", () => {
  it("prefers the stage the pipeline wrote into the details", () => {
    expect(eventStage({ eventType: "document.stage_failed", toStatus: "Failed", details: { stage: "Extract", attempt: 1 } })).toBe("Extract");
    expect(eventStage({ eventType: "document.reprocessed", toStatus: "Matched", details: { fromStage: "Extract" } })).toBe("Extract");
  });

  it("names a completed stage by the status it reached", () => {
    expect(eventStage({ eventType: "document.stage_completed", toStatus: "Normalized", details: null })).toBe("Normalize");
    expect(eventStage({ eventType: "document.stage_completed", toStatus: "Validated", details: {} })).toBe("Validate");
  });

  it("has no stage for human decisions and review routing", () => {
    expect(eventStage({ eventType: "document.approved", toStatus: "Approved", details: { comment: null } })).toBeNull();
    expect(eventStage({ eventType: "document.review_requested", toStatus: "NeedsReview", details: { reason: "LOW_CONFIDENCE" } })).toBeNull();
  });
});

describe("detailRows", () => {
  it("flattens nested objects and arrays into dotted leaf rows in key order", () => {
    const rows = detailRows({ stage: "Extract", attempt: 2, retry: false, error: "boom", flag: { code: "X", field: null }, tried: ["a", "b"], empty: {}, none: [] });
    expect(rows).toEqual([
      { path: "stage", value: "Extract", kind: "string" },
      { path: "attempt", value: "2", kind: "number" },
      { path: "retry", value: "false", kind: "boolean" },
      { path: "error", value: "boom", kind: "string" },
      { path: "flag.code", value: "X", kind: "string" },
      { path: "flag.field", value: "null", kind: "null" },
      { path: "tried[0]", value: "a", kind: "string" },
      { path: "tried[1]", value: "b", kind: "string" },
      { path: "empty", value: "{}", kind: "empty" },
      { path: "none", value: "[]", kind: "empty" },
    ]);
  });

  it("gives no rows for null and one named row for a scalar", () => {
    expect(detailRows(null)).toEqual([]);
    expect(detailRows(undefined)).toEqual([]);
    expect(detailRows("just text")).toEqual([{ path: "value", value: "just text", kind: "string" }]);
    expect(detailRows(42)).toEqual([{ path: "value", value: "42", kind: "number" }]);
  });

  it("stops at the row limit", () => {
    const wide = Object.fromEntries(Array.from({ length: 50 }, (_, i) => [`k${i}`, i]));
    expect(detailRows(wide, 10)).toHaveLength(10);
  });
});

describe("summarizeEvent", () => {
  it("picks the error, then reason, then message, then code", () => {
    expect(summarizeEvent({ eventType: "document.stage_failed", details: { stage: "Extract", error: "LLM_PROVIDER_UNAVAILABLE" } })).toBe("LLM_PROVIDER_UNAVAILABLE");
    expect(summarizeEvent({ eventType: "document.review_requested", details: { reason: "LOW_CONFIDENCE" } })).toBe("LOW_CONFIDENCE");
    expect(summarizeEvent({ eventType: "document.flagged", details: { code: "TOTAL_MISMATCH", severity: "Blocking" } })).toBe("TOTAL_MISMATCH");
  });

  it("lists corrected paths and falls back to null", () => {
    expect(summarizeEvent({ eventType: "document.corrected", details: { paths: ["invoiceNumber", "lines[0].quantity"] } })).toBe("invoiceNumber, lines[0].quantity");
    expect(summarizeEvent({ eventType: "document.corrected", details: { corrections: [{ path: "totalGross" }] } })).toBe("totalGross");
    expect(summarizeEvent({ eventType: "document.received", details: null })).toBeNull();
    expect(summarizeEvent({ eventType: "document.received", details: { attempt: 1 } })).toBeNull();
    expect(summarizeEvent({ eventType: "document.received", details: "plain" })).toBe("plain");
  });
});

describe("detailsJson", () => {
  it("pretty prints and survives cycles", () => {
    expect(detailsJson({ a: 1 })).toBe('{\n  "a": 1\n}');
    const cyclic: Record<string, unknown> = {};
    cyclic.self = cyclic;
    expect(detailsJson(cyclic)).toBe("[object Object]");
    expect(detailsJson(undefined)).toBe("null");
  });
});
