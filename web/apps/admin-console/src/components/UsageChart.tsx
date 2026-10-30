import { useTranslation } from "react-i18next";
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts";
import { ChartContainer, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@docproc/ui";
import { formatUsd, type UsageRow } from "@/lib/usage";

/**
 * Estimated cost per bucket as one series of thin bars (one measure, one axis; the table beside it carries the other
 * columns). The series colour is the design system's first chart token so light and dark mode are both covered.
 */
export function UsageChart({ rows, maxBars = 40 }: { rows: UsageRow[]; maxBars?: number }) {
  const { t, i18n } = useTranslation("admin");
  const config: ChartConfig = { costUsd: { label: t("usage.cost"), color: "var(--chart-1)" } };
  const data = rows.slice(0, maxBars).map((row) => ({ label: row.label, costUsd: row.costUsd, calls: row.calls }));
  return (
    <ChartContainer config={config} className="h-64 w-full" role="img" aria-label={t("usage.chartLabel")}>
      <BarChart data={data} margin={{ top: 8, right: 8, left: 8, bottom: 8 }} barCategoryGap="20%">
        <CartesianGrid vertical={false} strokeDasharray="3 3" />
        <XAxis dataKey="label" tickLine={false} axisLine={false} minTickGap={16} tickFormatter={(value: string) => (value.length > 14 ? `${value.slice(0, 12)}…` : value)} />
        <YAxis tickLine={false} axisLine={false} width={64} tickFormatter={(value: number) => formatUsd(value, i18n.language)} />
        <ChartTooltip
          cursor={{ fill: "var(--muted)" }}
          content={<ChartTooltipContent hideIndicator formatter={(value, _name, item) => `${formatUsd(Number(value), i18n.language)} · ${t("usage.callsCount", { count: Number((item.payload as { calls?: number } | undefined)?.calls ?? 0) })}`} />}
        />
        <Bar dataKey="costUsd" fill="var(--color-costUsd)" radius={[4, 4, 0, 0]} maxBarSize={28} isAnimationActive={false} />
      </BarChart>
    </ChartContainer>
  );
}
