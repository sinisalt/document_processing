import { describe, expect, it } from "vitest";
import { detailEntries, humanizeKey } from "./Timeline";

describe("timeline details", () => {
  it("lists an event's details as label and value instead of raw JSON", () => {
    expect(detailEntries({ stage: "Validate", paths: ["dueDate", "lines[2].lineNet"], retry: null, nested: { a: 1 } })).toEqual([
      ["stage", "Validate"],
      ["paths", "dueDate, lines[2].lineNet"],
      ["nested", '{"a":1}'],
    ]);
    expect(detailEntries(null)).toEqual([]);
    expect(detailEntries(["not", "an", "object"])).toEqual([]);
  });

  it("names flag codes and writes ratios as percentages, as the flag list above does", () => {
    const format = { yes: "Yes", no: "No", flagCode: (code: string) => (code === "LOW_CONFIDENCE" ? "Low confidence" : code), percent: (ratio: number) => `${Math.round(ratio * 100)}%` };
    expect(detailEntries({ flags: ["LOW_CONFIDENCE"], openFlags: ["LOW_CONFIDENCE", "NEW_RULE"], eligibleForAutoApproval: false, alwaysReview: true, threshold: 0.8 }, format)).toEqual([
      ["flags", "Low confidence"],
      ["openFlags", "Low confidence, NEW_RULE"],
      ["eligibleForAutoApproval", "No"],
      ["alwaysReview", "Yes"],
      ["threshold", "80%"],
    ]);
    expect(detailEntries({ code: "LOW_CONFIDENCE", confidence: 0.5, attempt: 2, flags: [] }, format)).toEqual([
      ["code", "Low confidence"],
      ["confidence", "50%"],
      ["attempt", "2"],
    ]);
  });

  it("labels a key without a translation readably", () => {
    expect(humanizeKey("fromStage")).toBe("From stage");
    expect(humanizeKey("retry_delay")).toBe("Retry delay");
  });
});
