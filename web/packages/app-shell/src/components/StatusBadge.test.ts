import { describe, expect, it } from "vitest";
import { documentStatusVariant, reviewTaskStatusVariant } from "./StatusBadge";

describe("status badge variants", () => {
  it("keeps red for a hard failure and amber for a state that waits for a person", () => {
    // Needs review, Failed and an open review task all used to be the same solid red.
    expect(documentStatusVariant.Failed).toBe("destructive");
    expect(documentStatusVariant.NeedsReview).toBe("warning");
    expect(reviewTaskStatusVariant.Open).toBe("warning");
  });

  it("uses red for nothing but Failed among the document statuses", () => {
    expect(Object.entries(documentStatusVariant).filter(([, variant]) => variant === "destructive").map(([status]) => status)).toEqual(["Failed"]);
  });
});
