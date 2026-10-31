import { describe, expect, it } from "vitest";
import { usageDays } from "./usage";

const bucket = (key: string, calls: number) => ({ key, calls, inputTokens: 100 * calls, outputTokens: 10 * calls, estimatedCostUsd: 0.01 * calls });

describe("usageDays", () => {
  it("fills every day of the window, with zeros where the API has no bucket", () => {
    const days = usageDays([bucket("2026-10-18", 1), bucket("2026-10-20", 3)], "2026-10-17", "2026-10-21");
    expect(days.map((d) => [d.day, d.calls])).toEqual([
      ["2026-10-17", 0],
      ["2026-10-18", 1],
      ["2026-10-19", 0],
      ["2026-10-20", 3],
      ["2026-10-21", 0],
    ]);
    expect(days[3]).toEqual({ day: "2026-10-20", calls: 3, tokens: 330, cost: 0.03 });
  });

  it("spans the 31 days of a window from Oct 1 to Oct 31, across the DST change", () => {
    const days = usageDays([bucket("2026-10-31", 2)], "2026-10-01", "2026-10-31");
    expect(days).toHaveLength(31);
    expect(days[0]?.day).toBe("2026-10-01");
    expect(days[25]?.day).toBe("2026-10-26");
    expect(days.at(-1)).toMatchObject({ day: "2026-10-31", calls: 2 });
  });

  it("keeps the API's buckets when the window cannot be read, and buckets outside it", () => {
    expect(usageDays([bucket("2026-10-18", 1)], undefined, undefined).map((d) => d.day)).toEqual(["2026-10-18"]);
    expect(usageDays([bucket("2026-10-18", 1)], "2026-10-20", "2026-10-19").map((d) => d.day)).toEqual(["2026-10-18"]);
    expect(usageDays([bucket("2026-10-22", 1)], "2026-10-20", "2026-10-21").map((d) => d.day)).toEqual(["2026-10-20", "2026-10-21", "2026-10-22"]);
  });
});
