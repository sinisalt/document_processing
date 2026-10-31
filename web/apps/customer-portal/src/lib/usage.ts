/** One day of `GET /llm-usage?groupBy=day` as the usage chart and table show it. */
export interface UsageDay {
  /** `YYYY-MM-DD`, the bucket key. */
  day: string;
  calls: number;
  tokens: number;
  cost: number;
}

export interface UsageBucketLike {
  key: string;
  calls: number | string;
  inputTokens: number | string;
  outputTokens: number | string;
  estimatedCostUsd: number | string;
}

const ISO_DAY = /^(\d{4})-(\d{2})-(\d{2})$/;
/** A window longer than this is not filled day by day (a malformed range must not build a huge array). */
const MAX_DAYS = 400;

function toUtcDay(value: string): number | null {
  const match = ISO_DAY.exec(value.slice(0, 10));
  if (!match) return null;
  return Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
}

const dayKey = (time: number) => new Date(time).toISOString().slice(0, 10);

/**
 * Every day of the window `from`..`to` (inclusive, the API's `DateOnly` bounds), with zeros for the days without a
 * call: the API returns only the days that have usage, and a chart of those alone spans less than the window its card
 * names. Buckets outside the window, and windows the helper cannot read, keep the API's buckets as they are.
 */
export function usageDays(buckets: readonly UsageBucketLike[], from: string | undefined, to: string | undefined): UsageDay[] {
  const toDay = (bucket: UsageBucketLike): UsageDay => ({
    day: bucket.key,
    calls: Number(bucket.calls),
    tokens: Number(bucket.inputTokens) + Number(bucket.outputTokens),
    cost: Number(bucket.estimatedCostUsd),
  });
  const start = from ? toUtcDay(from) : null;
  const end = to ? toUtcDay(to) : null;
  const days = start !== null && end !== null ? Math.round((end - start) / 86_400_000) + 1 : 0;
  if (days <= 0 || days > MAX_DAYS || buckets.some((bucket) => toUtcDay(bucket.key) === null)) return buckets.map(toDay);

  const byKey = new Map(buckets.map((bucket) => [bucket.key.slice(0, 10), toDay(bucket)]));
  const filled: UsageDay[] = [];
  for (let i = 0; i < days; i++) {
    const key = dayKey(start! + i * 86_400_000);
    filled.push(byKey.get(key) ?? { day: key, calls: 0, tokens: 0, cost: 0 });
    byKey.delete(key);
  }
  // A bucket the window does not cover (clock skew between the API and its bounds) is still shown, in order.
  return byKey.size > 0 ? [...filled, ...byKey.values()].sort((a, b) => a.day.localeCompare(b.day)) : filled;
}
