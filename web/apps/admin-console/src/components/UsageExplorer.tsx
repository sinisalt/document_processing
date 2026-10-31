import { useMemo, useState } from "react";
import { useQuery, type QueryKey } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Card, CardContent, CardDescription, CardHeader, CardTitle, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import { DataTable, createDataTableColumnHelper, formatDate, formatNumber, formatPercent, unwrap, useApi, type DataTableColumns } from "@docproc/app-shell";
import type { ApiClient } from "@docproc/api-client";
import { fillDays, formatTokens, formatUsd, monthRange, usageRows, type LlmUsageResponse, type UsageGrouping, type UsageRow } from "@/lib/usage";
import { UsageChart } from "./UsageChart";

export interface UsageQuery {
  from: string;
  to: string;
  groupBy: UsageGrouping;
}

/** Where the explorer reads from: the platform endpoint (`/llm/usage`) or the current tenant's (`/llm-usage`). */
export interface UsageSource {
  groupings: readonly UsageGrouping[];
  defaultGrouping: UsageGrouping;
  queryKey: (query: UsageQuery) => QueryKey;
  fetch: (client: ApiClient, query: UsageQuery) => Promise<LlmUsageResponse>;
  /** Resolves bucket keys to names (tenant id → tenant name). */
  labelFor?: (key: string) => string | undefined;
  enabled?: boolean;
}

export const platformUsageSource: UsageSource = {
  groupings: ["tenant", "model", "day"],
  defaultGrouping: "tenant",
  queryKey: (query) => ["llm", "platform", "usage", query],
  fetch: async (client, query) => unwrap(await client.GET("/api/v1/llm/usage", { params: { query } })),
};

export const tenantUsageSource: UsageSource = {
  groupings: ["day", "stage", "model"],
  defaultGrouping: "day",
  queryKey: (query) => ["llm", "usage", query],
  fetch: async (client, query) => unwrap(await client.GET("/api/v1/llm-usage", { params: { query } })),
};

const helper = createDataTableColumnHelper<UsageRow>();
const EMPTY: UsageRow[] = [];

/**
 * The usage explorer of docs/08: a date window and a grouping, the API's buckets as a sortable table with the total
 * underneath, and a cost chart when there is more than one bucket to compare.
 */
export function UsageExplorer({ source, title, description }: { source: UsageSource; title?: string; description?: string }) {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  // The current month, the window the dashboard's "AI spend this month" uses, so both start from the same figures.
  const [range, setRange] = useState(() => monthRange());
  const [groupBy, setGroupBy] = useState<UsageGrouping>(source.defaultGrouping);
  const query: UsageQuery = { from: range.from, to: range.to, groupBy };
  const validRange = range.from !== "" && range.to !== "" && range.from <= range.to;

  const usage = useQuery({
    queryKey: source.queryKey(query),
    queryFn: () => source.fetch(client, query),
    enabled: (source.enabled ?? true) && validRange,
  });

  const labelFor = source.labelFor;
  const rows = useMemo(() => {
    if (!usage.data) return EMPTY;
    const dayLabel = (key: string) => (groupBy === "day" ? formatDate(key, i18n.language) : undefined);
    const resolve = (key: string) => labelFor?.(key) ?? dayLabel(key) ?? (groupBy === "stage" ? t(`stages.${key}`, { defaultValue: key }) : undefined);
    return usageRows(usage.data, resolve);
  }, [usage.data, groupBy, labelFor, i18n.language, t]);

  // The chart orders days chronologically (with the empty ones), everything else by cost like the table.
  const chartRows = useMemo(() => {
    if (!usage.data) return EMPTY;
    if (groupBy !== "day") return rows;
    const filled = fillDays(usage.data.buckets, usage.data.from, usage.data.to);
    return usageRows({ buckets: filled, total: usage.data.total }, (key) => formatDate(key, i18n.language, { month: "short", day: "numeric" })).sort((a, b) => a.key.localeCompare(b.key));
  }, [usage.data, groupBy, rows, i18n.language]);

  const columns = useMemo<DataTableColumns<UsageRow>>(
    () =>
      helper.columns([
        helper.accessor("label", { header: t(`usage.groupBy.${groupBy}`), cell: (info) => <span className={groupBy === "tenant" && info.getValue() === info.row.original.key ? "font-mono text-xs" : undefined}>{info.getValue()}</span> }),
        helper.accessor("calls", { header: t("usage.calls"), cell: (info) => <span className="tabular-nums">{formatNumber(info.getValue(), i18n.language)}</span> }),
        helper.accessor("errors", { header: t("usage.errors"), cell: (info) => <span className="tabular-nums">{formatNumber(info.getValue(), i18n.language)}</span> }),
        helper.accessor("errorRate", { header: t("usage.errorRate"), cell: (info) => <span className="tabular-nums">{formatPercent(info.getValue(), i18n.language, 1)}</span> }),
        helper.accessor("inputTokens", { header: t("usage.inputTokens"), cell: (info) => <span className="tabular-nums" title={formatNumber(info.getValue(), i18n.language)}>{formatTokens(info.getValue(), i18n.language)}</span> }),
        helper.accessor("outputTokens", { header: t("usage.outputTokens"), cell: (info) => <span className="tabular-nums" title={formatNumber(info.getValue(), i18n.language)}>{formatTokens(info.getValue(), i18n.language)}</span> }),
        helper.accessor("cacheReadTokens", { header: t("usage.cacheReadTokens"), cell: (info) => <span className="tabular-nums" title={formatNumber(info.getValue(), i18n.language)}>{formatTokens(info.getValue(), i18n.language)}</span> }),
        helper.accessor("costUsd", { header: t("usage.cost"), cell: (info) => <span className="tabular-nums">{formatUsd(info.getValue(), i18n.language)}</span> }),
        helper.accessor("share", { header: t("usage.share"), cell: (info) => <span className="tabular-nums">{formatPercent(info.getValue(), i18n.language)}</span> }),
      ]),
    [t, groupBy, i18n.language],
  );

  const total = usage.data?.total;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{title ?? t("usage.title")}</CardTitle>
        <CardDescription>{description ?? t("usage.description")}</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="flex flex-wrap items-end gap-3" role="group" aria-label={t("usage.filters")}>
          <div className="grid gap-1">
            <Label htmlFor="usage-from">{t("usage.from")}</Label>
            <Input id="usage-from" type="date" value={range.from} max={range.to} onChange={(event) => setRange((r) => ({ ...r, from: event.target.value }))} className="w-40" />
          </div>
          <div className="grid gap-1">
            <Label htmlFor="usage-to">{t("usage.to")}</Label>
            <Input id="usage-to" type="date" value={range.to} min={range.from} onChange={(event) => setRange((r) => ({ ...r, to: event.target.value }))} className="w-40" />
          </div>
          <div className="grid gap-1">
            <Label htmlFor="usage-group-by">{t("usage.groupByLabel")}</Label>
            <Select value={groupBy} onValueChange={(value) => setGroupBy(value as UsageGrouping)}>
              <SelectTrigger id="usage-group-by" className="w-40" aria-label={t("usage.groupByLabel")}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {source.groupings.map((g) => (
                  <SelectItem key={g} value={g}>
                    {t(`usage.groupBy.${g}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          {!validRange && (
            <p role="alert" className="text-sm text-destructive">
              {t("usage.invalidRange")}
            </p>
          )}
        </div>

        {chartRows.length > 1 && <UsageChart rows={chartRows} />}

        <DataTable
          columns={columns}
          data={rows}
          getRowId={(row) => row.key}
          isLoading={usage.isPending && validRange}
          query={usage}
          initialSorting={[{ id: "costUsd", desc: true }]}
          caption={t("usage.tableCaption")}
          emptyTitle={t("usage.empty")}
          emptyDescription={t("usage.emptyDescription")}
          footer={
            total ? (
              <span className="tabular-nums" aria-live="polite">
                {t("usage.total", {
                  // Each count picks its own plural ("1 error", not "1 errors") and keeps the locale's digit grouping.
                  calls: t("usage.totalCalls", { count: Number(total.calls), value: formatNumber(total.calls, i18n.language) }),
                  errors: t("usage.totalErrors", { count: Number(total.errors), value: formatNumber(total.errors, i18n.language) }),
                  cost: formatUsd(total.estimatedCostUsd, i18n.language),
                })}
              </span>
            ) : undefined
          }
        />
      </CardContent>
    </Card>
  );
}
