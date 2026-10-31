import { describe, expect, it } from "vitest";
import { axisLabel, daysBetween, fillDays, formatUsd, isoDate, lastDaysRange, monthRange, sumBuckets, tokensOf, usageRows, type LlmUsageBucket } from "./usage";

function bucket(key: string, overrides: Partial<LlmUsageBucket> = {}): LlmUsageBucket {
  return { key, calls: 10, errors: 1, inputTokens: 1000, outputTokens: 200, cacheReadTokens: 50, cacheWriteTokens: 25, reasoningTokens: 5, estimatedCostUsd: 0.5, ...overrides };
}

describe("date windows", () => {
  it("monthRange starts on the first of the UTC month", () => {
    expect(monthRange(new Date("2026-10-30T23:30:00Z"))).toEqual({ from: "2026-10-01", to: "2026-10-30" });
    expect(monthRange(new Date("2026-01-01T00:00:00Z"))).toEqual({ from: "2026-01-01", to: "2026-01-01" });
  });

  it("lastDaysRange counts today as a day and crosses month boundaries", () => {
    expect(lastDaysRange(30, new Date("2026-10-30T12:00:00Z"))).toEqual({ from: "2026-10-01", to: "2026-10-30" });
    expect(lastDaysRange(1, new Date("2026-03-01T12:00:00Z"))).toEqual({ from: "2026-03-01", to: "2026-03-01" });
    expect(lastDaysRange(2, new Date("2026-03-01T12:00:00Z"))).toEqual({ from: "2026-02-28", to: "2026-03-01" });
  });

  it("isoDate keeps the UTC day", () => {
    expect(isoDate(new Date("2026-10-30T23:59:59Z"))).toBe("2026-10-30");
  });

  it("daysBetween is inclusive and empty when inverted or malformed", () => {
    expect(daysBetween("2026-02-27", "2026-03-02")).toEqual(["2026-02-27", "2026-02-28", "2026-03-01", "2026-03-02"]);
    expect(daysBetween("2026-03-02", "2026-03-01")).toEqual([]);
    expect(daysBetween("nope", "2026-03-01")).toEqual([]);
  });
});

describe("aggregation", () => {
  it("tokensOf adds every token kind and accepts the client's string numbers", () => {
    expect(tokensOf(bucket("a"))).toBe(1280);
    expect(tokensOf(bucket("a", { inputTokens: "7", outputTokens: "3", cacheReadTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0 }))).toBe(10);
  });

  it("sumBuckets matches a field-by-field total and rounds the cost", () => {
    const total = sumBuckets([bucket("a"), bucket("b", { estimatedCostUsd: 0.1, calls: "5", errors: 0 })]);
    expect(total.key).toBe("total");
    expect(total.calls).toBe(15);
    expect(total.errors).toBe(1);
    expect(total.inputTokens).toBe(2000);
    expect(total.estimatedCostUsd).toBe(0.6);
    expect(sumBuckets([]).calls).toBe(0);
  });

  it("usageRows sorts by cost, labels keys and derives shares and error rates", () => {
    const rows = usageRows(
      { buckets: [bucket("t1", { estimatedCostUsd: 1 }), bucket("t2", { estimatedCostUsd: 3, calls: 0, errors: 0 })], total: bucket("total", { estimatedCostUsd: 4 }) },
      (key) => (key === "t1" ? "Tenant One" : undefined),
    );
    expect(rows.map((r) => r.key)).toEqual(["t2", "t1"]);
    expect(rows[0]!.label).toBe("t2");
    expect(rows[1]!.label).toBe("Tenant One");
    expect(rows[0]!.share).toBeCloseTo(0.75);
    expect(rows[1]!.share).toBeCloseTo(0.25);
    expect(rows[0]!.errorRate).toBe(0);
    expect(rows[1]!.errorRate).toBeCloseTo(0.1);
    expect(rows[1]!.tokens).toBe(1280);
  });

  it("usageRows copes with a zero total", () => {
    const rows = usageRows({ buckets: [bucket("a", { estimatedCostUsd: 0 })], total: bucket("total", { estimatedCostUsd: 0 }) });
    expect(rows[0]!.share).toBe(0);
  });

  it("fillDays pads the window with zero days in order", () => {
    const filled = fillDays([bucket("2026-10-03")], "2026-10-01", "2026-10-03");
    expect(filled.map((b) => b.key)).toEqual(["2026-10-01", "2026-10-02", "2026-10-03"]);
    expect(filled[0]!.calls).toBe(0);
    expect(filled[2]!.calls).toBe(10);
  });
});

describe("formatting", () => {
  it("formatUsd shows four decimals under a dollar and two above", () => {
    expect(formatUsd(0.01234, "en")).toBe("$0.0123");
    expect(formatUsd(12.5, "en")).toBe("$12.50");
    expect(formatUsd(0, "en")).toBe("$0.00");
    expect(formatUsd("3.25", "en")).toBe("$3.25");
    expect(formatUsd(null, "en")).toBe("—");
  });
});

describe("axisLabel", () => {
  it("keeps a tenant name whole while there are few bars and shortens it as they crowd", () => {
    expect(axisLabel("Gasthaus zur Post GmbH", 2)).toBe("Gasthaus zur Post GmbH");
    expect(axisLabel("Gasthaus zur Post GmbH", 6)).toBe("Gasthaus zur Post…");
    expect(axisLabel("Gasthaus zur Post GmbH", 30)).toBe("Gasthaus zur…");
    expect(axisLabel("2026-10-21", 30)).toBe("2026-10-21");
  });
});
