import type { components } from "@docproc/api-client";
import { formatMoney, formatNumber, toNumber, type Numeric } from "@docproc/app-shell";

export type LlmUsageBucket = components["schemas"]["LlmUsageBucket"];
export type LlmUsageResponse = components["schemas"]["LlmUsageResponse"];

/** The groupings each usage endpoint accepts (LlmAdminEndpoints: PlatformGroupings / TenantGroupings). */
export const platformGroupings = ["tenant", "model", "day"] as const;
export const tenantGroupings = ["day", "stage", "model"] as const;
export type PlatformGrouping = (typeof platformGroupings)[number];
export type TenantGrouping = (typeof tenantGroupings)[number];
export type UsageGrouping = PlatformGrouping | TenantGrouping;

/** `2026-10-30T13:00:00Z` → `2026-10-30` (UTC calendar date, which is what the API's inclusive `from`/`to` mean). */
export function isoDate(date: Date): string {
  return date.toISOString().slice(0, 10);
}

/** First day of the (UTC) month `now` is in, through `now`: the dashboard's "this month" spend window. */
export function monthRange(now: Date = new Date()): { from: string; to: string } {
  const first = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  return { from: isoDate(first), to: isoDate(now) };
}

/** The last `days` calendar days ending today (inclusive), the usage explorer's default window. */
export function lastDaysRange(days: number, now: Date = new Date()): { from: string; to: string } {
  const start = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() - Math.max(0, days - 1)));
  return { from: isoDate(start), to: isoDate(now) };
}

/** Input + output + cache (read and write) + reasoning tokens of a bucket. */
export function tokensOf(bucket: LlmUsageBucket): number {
  return (
    (toNumber(bucket.inputTokens) ?? 0) +
    (toNumber(bucket.outputTokens) ?? 0) +
    (toNumber(bucket.cacheReadTokens) ?? 0) +
    (toNumber(bucket.cacheWriteTokens) ?? 0) +
    (toNumber(bucket.reasoningTokens) ?? 0)
  );
}

export function emptyBucket(key: string): LlmUsageBucket {
  return { key, calls: 0, errors: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0, estimatedCostUsd: 0 };
}

/** Adds buckets field by field; the client-side counterpart of the API's `total`, used to cross-check and for subsets. */
export function sumBuckets(buckets: readonly LlmUsageBucket[], key = "total"): LlmUsageBucket {
  const total = emptyBucket(key);
  for (const b of buckets) {
    total.calls = (toNumber(total.calls) ?? 0) + (toNumber(b.calls) ?? 0);
    total.errors = (toNumber(total.errors) ?? 0) + (toNumber(b.errors) ?? 0);
    total.inputTokens = (toNumber(total.inputTokens) ?? 0) + (toNumber(b.inputTokens) ?? 0);
    total.outputTokens = (toNumber(total.outputTokens) ?? 0) + (toNumber(b.outputTokens) ?? 0);
    total.cacheReadTokens = (toNumber(total.cacheReadTokens) ?? 0) + (toNumber(b.cacheReadTokens) ?? 0);
    total.cacheWriteTokens = (toNumber(total.cacheWriteTokens) ?? 0) + (toNumber(b.cacheWriteTokens) ?? 0);
    total.reasoningTokens = (toNumber(total.reasoningTokens) ?? 0) + (toNumber(b.reasoningTokens) ?? 0);
    total.estimatedCostUsd = round6((toNumber(total.estimatedCostUsd) ?? 0) + (toNumber(b.estimatedCostUsd) ?? 0));
  }
  return total;
}

function round6(n: number): number {
  return Math.round(n * 1_000_000) / 1_000_000;
}

export interface UsageRow {
  key: string;
  /** What the table shows for the key (a tenant name instead of its id, a formatted day); the key itself by default. */
  label: string;
  calls: number;
  errors: number;
  /** Errors as a fraction of calls, 0 when there were no calls. */
  errorRate: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
  tokens: number;
  costUsd: number;
  /** This row's share of the total cost (0..1), 0 when nothing was spent. */
  share: number;
}

/**
 * Turns the API's buckets into table rows sorted by cost (highest first), with totals-derived shares. `labelFor`
 * resolves a key to a display label (tenant id → name); missing labels fall back to the key.
 */
export function usageRows(response: Pick<LlmUsageResponse, "buckets" | "total">, labelFor?: (key: string) => string | undefined): UsageRow[] {
  const totalCost = toNumber(response.total.estimatedCostUsd) ?? 0;
  return response.buckets
    .map((b) => {
      const calls = toNumber(b.calls) ?? 0;
      const errors = toNumber(b.errors) ?? 0;
      const costUsd = toNumber(b.estimatedCostUsd) ?? 0;
      return {
        key: b.key,
        label: labelFor?.(b.key) ?? b.key,
        calls,
        errors,
        errorRate: calls > 0 ? errors / calls : 0,
        inputTokens: toNumber(b.inputTokens) ?? 0,
        outputTokens: toNumber(b.outputTokens) ?? 0,
        cacheReadTokens: toNumber(b.cacheReadTokens) ?? 0,
        cacheWriteTokens: toNumber(b.cacheWriteTokens) ?? 0,
        reasoningTokens: toNumber(b.reasoningTokens) ?? 0,
        tokens: tokensOf(b),
        costUsd,
        share: totalCost > 0 ? costUsd / totalCost : 0,
      };
    })
    .sort((a, b) => b.costUsd - a.costUsd || a.key.localeCompare(b.key));
}

/** Every calendar day from `from` to `to` inclusive (both `YYYY-MM-DD`), in order; empty when the range is inverted. */
export function daysBetween(from: string, to: string): string[] {
  const start = Date.parse(`${from}T00:00:00Z`);
  const end = Date.parse(`${to}T00:00:00Z`);
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) return [];
  const days: string[] = [];
  for (let t = start; t <= end; t += 86_400_000) days.push(isoDate(new Date(t)));
  return days;
}

/**
 * For the `day` grouping: one bucket per calendar day of the window, zero where the API returned none, in date
 * order, so a chart shows the gaps instead of squeezing them out.
 */
export function fillDays(buckets: readonly LlmUsageBucket[], from: string, to: string): LlmUsageBucket[] {
  const byKey = new Map(buckets.map((b) => [b.key, b] as const));
  return daysBetween(from, to).map((day) => byKey.get(day) ?? emptyBucket(day));
}

/** `0.01234` → `$0.0123`; small LLM amounts need more than two decimals to be readable. */
export function formatUsd(value: Numeric, locale?: string): string {
  const n = toNumber(value);
  if (n === null) return formatMoney(null, "USD", locale);
  const digits = Math.abs(n) > 0 && Math.abs(n) < 1 ? 4 : 2;
  return new Intl.NumberFormat(locale ?? undefined, { style: "currency", currency: "USD", minimumFractionDigits: 2, maximumFractionDigits: digits }).format(n);
}

/**
 * An x-axis label of the usage chart: whole while the bars leave room for it (a handful of tenants), cut with an
 * ellipsis only as they crowd (thirty days).
 */
export function axisLabel(value: string, barCount: number): string {
  const max = barCount <= 4 ? 32 : barCount <= 8 ? 20 : 14;
  return value.length > max ? `${value.slice(0, max - 2).trimEnd()}…` : value;
}

/** `1234567` → `1.2M` style token counts for tight table cells. */
export function formatTokens(value: Numeric, locale?: string): string {
  return formatNumber(value, locale, { notation: "compact", maximumFractionDigits: 1 });
}
