import { describe, expect, it } from "vitest";
import { canReprocessFrom, defaultReprocessStage, furthestPipelineStatus, reprocessStages, type DocumentStatus } from "./documents";

const event = (fromStatus: DocumentStatus | null, toStatus: DocumentStatus | null) => ({ fromStatus, toStatus });

describe("reprocess stage mapping (DocumentEndpoints.ReprocessStart)", () => {
  it("finds the furthest progress state in the timeline and the current status", () => {
    expect(furthestPipelineStatus([], "Failed")).toBe("Received");
    expect(furthestPipelineStatus([event(null, "Received"), event("Received", "Failed")], "Failed")).toBe("Received");
    expect(furthestPipelineStatus([event("Received", "Normalized"), event("Normalized", "Split"), event("Split", "Failed")], "Failed")).toBe("Split");
    expect(furthestPipelineStatus([event("Matched", "Extracted"), event("Extracted", "Validated"), event("Validated", "NeedsReview")], "NeedsReview")).toBe("Validated");
    // A rerun went back to Received after getting further: the furthest state still counts.
    expect(furthestPipelineStatus([event("Extracted", "Validated"), event("Failed", "Received")], "Received")).toBe("Validated");
  });

  it("a document that failed in Normalize can only rerun Normalize", () => {
    const furthest = furthestPipelineStatus([event(null, "Received"), event("Received", "Failed")], "Failed");
    expect(defaultReprocessStage(furthest)).toBe("Normalize");
    expect(reprocessStages.filter((stage) => canReprocessFrom(stage, furthest))).toEqual(["Normalize"]);
  });

  it("maps each start status to the latest stage it allows", () => {
    const cases: [DocumentStatus, string, string[]][] = [
      ["Received", "Normalize", ["Normalize"]],
      ["Normalized", "Normalize", ["Normalize"]],
      ["Split", "Classify", ["Normalize", "Classify"]],
      ["Classified", "Classify", ["Normalize", "Classify"]],
      ["Matched", "Extract", ["Normalize", "Classify", "Extract"]],
      ["Extracted", "Validate", ["Normalize", "Classify", "Extract", "Validate"]],
      ["Validated", "Validate", ["Normalize", "Classify", "Extract", "Validate"]],
    ];
    for (const [furthest, latest, allowed] of cases) {
      expect(defaultReprocessStage(furthest), furthest).toBe(latest);
      expect(reprocessStages.filter((stage) => canReprocessFrom(stage, furthest)), furthest).toEqual(allowed);
    }
  });
});
