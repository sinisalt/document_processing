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

  it("labels a key without a translation readably", () => {
    expect(humanizeKey("fromStage")).toBe("From stage");
    expect(humanizeKey("retry_delay")).toBe("Retry delay");
  });
});
