import { describe, expect, it } from "vitest";
import type { DocumentEvent } from "./queries";
import { validationState } from "./validation";

let nextId = 1;
const event = (eventType: string, fromStatus: DocumentEvent["fromStatus"], toStatus: DocumentEvent["toStatus"]): DocumentEvent => ({
  id: nextId++,
  eventType,
  fromStatus,
  toStatus,
  actor: null,
  details: null,
  at: "2026-10-30T10:00:00Z",
});

const pipeline = [
  event("document.received", null, "Received"),
  event("document.stage_completed", "Received", "Normalized"),
  event("document.stage_completed", "Matched", "Extracted"),
  event("document.stage_completed", "Extracted", "Validated"),
  event("document.review_requested", "Validated", "NeedsReview"),
];

describe("validationState", () => {
  it("passes when the last validation is newer than any correction", () => {
    expect(validationState("NeedsReview", pipeline)).toBe("passed");
    expect(validationState("Approved", [...pipeline, event("document.approved", "NeedsReview", "Approved")])).toBe("passed");
  });

  it("is stale after a correction the API did not re-validate", () => {
    expect(validationState("NeedsReview", [...pipeline, event("document.corrected", "NeedsReview", "NeedsReview")])).toBe("stale");
  });

  it("passes again once a reprocess from Validate ran after the correction", () => {
    const events = [
      ...pipeline,
      event("document.corrected", "NeedsReview", "NeedsReview"),
      event("document.reprocessed", "NeedsReview", "Extracted"),
      event("document.stage_completed", "Extracted", "Validated"),
      event("document.review_requested", "Validated", "NeedsReview"),
    ];
    expect(validationState("NeedsReview", events)).toBe("passed");
  });

  it("is not validated for a document that never reached Validate, or was reprocessed since", () => {
    expect(validationState("Failed", [event("document.received", null, "Received"), event("document.stage_failed", "Received", "Failed")])).toBe("notValidated");
    expect(validationState("Failed", [...pipeline, event("document.reprocessed", "NeedsReview", "Matched"), event("document.stage_failed", "Matched", "Failed")])).toBe("notValidated");
    expect(validationState("Extracted", [])).toBe("notValidated");
  });

  it("does not advise a reprocess on a decided document that cannot be reprocessed", () => {
    const corrected = [...pipeline, event("document.corrected", "NeedsReview", "NeedsReview")];
    expect(validationState("Approved", [...corrected, event("document.approved", "NeedsReview", "Approved")])).toBe("staleSettled");
    expect(validationState("AutoApproved", [...corrected, event("document.auto_approved", "Validated", "AutoApproved")])).toBe("staleSettled");
    // Rejected documents can be reprocessed, so the advice stands.
    expect(validationState("Rejected", [...corrected, event("document.rejected", "NeedsReview", "Rejected")])).toBe("stale");
  });

  it("treats a timeline that ends in another status than the document's as outdated", () => {
    // Fetched right after a reprocess, before Validate ran again: the document is NeedsReview by now.
    const beforeRevalidation = [...pipeline, event("document.reprocessed", "NeedsReview", "Extracted")];
    expect(validationState("NeedsReview", beforeRevalidation)).toBe("passed");
    expect(validationState("Extracted", beforeRevalidation)).toBe("notValidated");
  });

  it("falls back to the status while the timeline is not loaded", () => {
    expect(validationState("NeedsReview", undefined)).toBe("passed");
    expect(validationState("Failed", undefined)).toBe("notValidated");
    expect(validationState("Extracted", undefined)).toBe("notValidated");
  });
});
