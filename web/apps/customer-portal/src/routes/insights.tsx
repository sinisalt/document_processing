import { useMemo, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import type { components } from "@docproc/api-client";
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Skeleton, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@docproc/ui";
import { EmptyState, PageHeader, formatMoney, queryKeys, relativeTime, unwrap, useApi, useAuth } from "@docproc/app-shell";
import { insightsWindow, percent } from "@/lib/insights";

type SupplierRow = components["schemas"]["SupplierAutomation"];

export const Route = createFileRoute("/insights")({ component: InsightsPage });

const windows = [7, 30, 90] as const;

/**
 * Insights > Onboarding (docs/11 §17, §19 item 8): whom to onboard next and what needs a look, from
 * `GET /insights/automation`. Suppliers without layouts by volume with "open sample", "create layout" and "add source
 * rule"; automation per supplier; the Unknown rate per channel and source rule; degraded layouts, layouts that have
 * not matched for 180 days (advisory), Candidates with documents waiting; tenant rules reviewers often override;
 * delivery failures per destination; open master-data conflicts.
 */
function InsightsPage() {
  const { t } = useTranslation();
  const { user } = useAuth();
  if (user?.scope !== "tenant") return <EmptyState title={t("insights.platformScopeTitle")} description={t("insights.platformScope")} />;
  return <InsightsScreen />;
}

function InsightsScreen() {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const [days, setDays] = useState<(typeof windows)[number]>(30);
  const { from, to } = useMemo(() => insightsWindow(days, new Date()), [days]);
  const insights = useQuery({
    queryKey: queryKeys.insights.automation(from, to),
    queryFn: async () => unwrap(await client.GET("/api/v1/insights/automation", { params: { query: { from, to } } })),
  });
  const data = insights.data;
  const money = (value: number | string) => formatMoney(Number(value), "USD", i18n.language);
  return (
    <>
      <PageHeader
        title={t("insights.title")}
        description={t("insights.description")}
        actions={
          <Select value={String(days)} onValueChange={(value) => setDays(Number(value) as (typeof windows)[number])}>
            <SelectTrigger className="w-40" aria-label={t("insights.window")} data-testid="insights-window">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {windows.map((d) => (
                <SelectItem key={d} value={String(d)}>
                  {t("insights.lastDays", { count: d })}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        }
      />
      {!data ? (
        <Skeleton className="h-64 w-full" aria-busy />
      ) : (
        <div className="grid gap-4" data-testid="insights">
          <div className="grid gap-4 sm:grid-cols-3">
            <Kpi title={t("insights.kpis.documents")} value={String(Number(data.documents))} />
            <Kpi title={t("insights.kpis.autoApproved")} value={percent(Number(data.autoApproved), Number(data.documents), i18n.language)} />
            <Kpi title={t("insights.kpis.unknown")} value={percent(Number(data.unknown), Number(data.documents), i18n.language)} />
          </div>

          <Section title={t("insights.onboardNext.title")} description={t("insights.onboardNext.description")} testId="insights-onboard-next">
            {data.suppliersWithoutLayouts.length === 0 ? (
              <Empty text={t("insights.onboardNext.none")} />
            ) : (
              <SupplierTable rows={data.suppliersWithoutLayouts} money={money} actions />
            )}
          </Section>

          <Section title={t("insights.suppliers.title")} description={t("insights.suppliers.description")} testId="insights-suppliers">
            {data.suppliers.length === 0 ? <Empty text={t("insights.suppliers.none")} /> : <SupplierTable rows={data.suppliers} money={money} />}
          </Section>

          <div className="grid gap-4 lg:grid-cols-2">
            <Section title={t("insights.unknown.channels")} testId="insights-unknown-channels">
              <RateTable rows={data.unknownByChannel} />
            </Section>
            <Section title={t("insights.unknown.rules")} testId="insights-unknown-rules">
              <RateTable rows={data.unknownBySourceRule} />
            </Section>
          </div>

          <Section title={t("insights.layouts.title")} description={t("insights.layouts.description")} testId="insights-layouts">
            {data.degradedLayouts.length + data.staleLayouts.length + data.candidatesWaiting.length === 0 ? (
              <Empty text={t("insights.layouts.none")} />
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("insights.layouts.layout")}</TableHead>
                    <TableHead>{t("insights.columns.supplier")}</TableHead>
                    <TableHead>{t("insights.layouts.state")}</TableHead>
                    <TableHead>{t("insights.layouts.lastMatched")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {[
                    ...data.degradedLayouts.map((l) => ({ ...l, why: "degraded" as const })),
                    ...data.candidatesWaiting.map((l) => ({ ...l, why: "waiting" as const })),
                    ...data.staleLayouts.map((l) => ({ ...l, why: "stale" as const })),
                  ].map((l) => (
                    <TableRow key={`${l.why}:${l.layoutId}:${String(l.version)}`} data-why={l.why} data-layout={l.layoutKey}>
                      <TableCell className="font-mono text-xs">
                        {l.layoutKey}
                        {l.version !== null && ` v${Number(l.version)}`} · {l.typeKey}
                      </TableCell>
                      <TableCell>{l.supplierName ?? "—"}</TableCell>
                      <TableCell>
                        <Badge variant={l.why === "degraded" ? "destructive" : l.why === "waiting" ? "warning" : "outline"}>{t(`insights.layouts.why.${l.why}`, { count: Number(l.waiting) })}</Badge>
                      </TableCell>
                      <TableCell className="text-xs">{l.lastMatchedAt ? relativeTime(l.lastMatchedAt, i18n.language) : t("insights.layouts.never")}</TableCell>
                      <TableCell>
                        <Button size="xs" variant="ghost" asChild>
                          <Link to="/suppliers" search={{ supplier: l.ownerId, tab: "layouts" }}>
                            {t("insights.actions.openLayouts")}
                          </Link>
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </Section>

          <div className="grid gap-4 lg:grid-cols-2">
            <Section title={t("insights.rules.title")} description={t("insights.rules.description")} testId="insights-rules">
              {data.overriddenRules.length === 0 ? (
                <Empty text={t("insights.rules.none")} />
              ) : (
                <Table>
                  <TableBody>
                    {data.overriddenRules.map((r) => (
                      <TableRow key={r.ruleId}>
                        <TableCell>
                          <Link to="/settings" search={{ tab: "types", type: r.typeKey }} className="underline-offset-2 hover:underline">
                            {r.name}
                          </Link>
                          <span className="block font-mono text-xs text-muted-foreground">{r.typeKey}</span>
                        </TableCell>
                        <TableCell className="text-xs">{t("insights.rules.counts", { matched: Number(r.matched), overridden: Number(r.overridden) })}</TableCell>
                        <TableCell>
                          <Badge variant={Number(r.rate) >= 0.1 ? "warning" : "outline"}>{percent(Number(r.overridden), Math.max(Number(r.matched), Number(r.overridden)), i18n.language)}</Badge>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </Section>
            <Section title={t("insights.deliveries.title")} testId="insights-deliveries">
              {data.destinations.length === 0 ? (
                <Empty text={t("insights.deliveries.none")} />
              ) : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("insights.deliveries.destination")}</TableHead>
                      <TableHead>{t("insights.deliveries.total")}</TableHead>
                      <TableHead>{t("insights.deliveries.failed")}</TableHead>
                      <TableHead>{t("insights.deliveries.deadLettered")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.destinations.map((d) => (
                      <TableRow key={d.destinationId} data-destination={d.name}>
                        <TableCell>{d.name}</TableCell>
                        <TableCell className="tabular-nums">{Number(d.total)}</TableCell>
                        <TableCell className="tabular-nums">{Number(d.failed)}</TableCell>
                        <TableCell className={Number(d.deadLettered) > 0 ? "font-medium text-destructive tabular-nums" : "tabular-nums"}>{Number(d.deadLettered)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </Section>
          </div>

          <Section title={t("insights.masterData.title")} testId="insights-master-data">
            {data.masterDataConflicts.length === 0 ? (
              <Empty text={t("insights.masterData.none")} />
            ) : (
              <ul className="grid gap-1 text-sm">
                {data.masterDataConflicts.map((c) => (
                  <li key={c.runId} className="flex flex-wrap items-center gap-2">
                    <Badge variant="warning">{t("insights.masterData.conflicts", { count: Number(c.conflicts) })}</Badge>
                    <span>{c.name}</span>
                    <span className="text-xs text-muted-foreground">{relativeTime(c.at, i18n.language)}</span>
                    <Link to="/settings" search={{ tab: "integrations", section: "masterData" }} className="text-xs underline underline-offset-2">
                      {t("insights.actions.openMasterData")}
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </Section>
        </div>
      )}
    </>
  );
}

function Kpi({ title, value }: { title: string; value: string }) {
  return (
    <Card>
      <CardHeader className="pb-2">
        <CardDescription>{title}</CardDescription>
        <CardTitle className="text-2xl tabular-nums">{value}</CardTitle>
      </CardHeader>
    </Card>
  );
}

function Section({ title, description, testId, children }: { title: string; description?: string; testId: string; children: ReactNode }) {
  return (
    <Card data-testid={testId}>
      <CardHeader>
        <CardTitle className="text-base">{title}</CardTitle>
        {description && <CardDescription>{description}</CardDescription>}
      </CardHeader>
      <CardContent>{children}</CardContent>
    </Card>
  );
}

function Empty({ text }: { text: string }) {
  return <p className="text-sm text-muted-foreground">{text}</p>;
}

function SupplierTable({ rows, money, actions = false }: { rows: readonly SupplierRow[]; money: (value: number | string) => string; actions?: boolean }) {
  const { t, i18n } = useTranslation();
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("insights.columns.supplier")}</TableHead>
          <TableHead>{t("insights.columns.documents")}</TableHead>
          <TableHead>{t("insights.columns.autoApproval")}</TableHead>
          <TableHead>{t("insights.columns.corrections")}</TableHead>
          <TableHead>{t("insights.columns.llmCost")}</TableHead>
          <TableHead>{t("insights.columns.layouts")}</TableHead>
          <TableHead />
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((s) => (
          <TableRow key={s.supplierId} data-supplier={s.name}>
            <TableCell className="font-medium">
              {s.name}
              {Number(s.openNewLayout) > 0 && (
                <Badge variant="warning" className="ml-2">
                  {t("insights.columns.newLayout", { count: Number(s.openNewLayout) })}
                </Badge>
              )}
            </TableCell>
            <TableCell className="tabular-nums">{Number(s.documents)}</TableCell>
            <TableCell className="tabular-nums">{percent(Number(s.autoApproved), Number(s.documents), i18n.language)}</TableCell>
            <TableCell className="tabular-nums">{Number(s.correctionsPerDocument).toLocaleString(i18n.language, { maximumFractionDigits: 2 })}</TableCell>
            <TableCell className="tabular-nums">{money(s.llmCostUsd)}</TableCell>
            <TableCell className="text-xs">
              {s.layouts.length === 0
                ? t("insights.columns.noLayouts")
                : s.layouts.map((l) => (
                    <span key={l.layoutId} className="block">
                      {l.typeKey}: <span className="font-mono">{l.layoutKey}</span> ({t(`insights.layoutStates.${l.state}`, { defaultValue: l.state })})
                    </span>
                  ))}
            </TableCell>
            <TableCell>
              {actions && (
                <div className="flex flex-wrap justify-end gap-1">
                  {s.sampleDocumentId && (
                    <Button size="xs" variant="ghost" asChild>
                      <Link to="/documents/$documentId" params={{ documentId: s.sampleDocumentId }} data-testid="insights-open-sample">
                        {t("insights.actions.openSample")}
                      </Link>
                    </Button>
                  )}
                  <Button size="xs" variant="ghost" asChild>
                    <Link to="/suppliers" search={{ supplier: s.supplierId, tab: "layouts" }} data-testid="insights-create-layout">
                      {t("insights.actions.createLayout")}
                    </Link>
                  </Button>
                  <Button size="xs" variant="ghost" asChild>
                    <Link to="/suppliers" search={{ supplier: s.supplierId, tab: "routing" }} data-testid="insights-add-source-rule">
                      {t("insights.actions.addSourceRule")}
                    </Link>
                  </Button>
                </div>
              )}
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function RateTable({ rows }: { rows: readonly components["schemas"]["UnknownRate"][] }) {
  const { t, i18n } = useTranslation();
  if (rows.length === 0) return <Empty text={t("insights.unknown.none")} />;
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("insights.unknown.name")}</TableHead>
          <TableHead>{t("insights.columns.documents")}</TableHead>
          <TableHead>{t("insights.unknown.rate")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((r) => (
          <TableRow key={r.id} data-unknown={r.name}>
            <TableCell>{r.name}</TableCell>
            <TableCell className="tabular-nums">{Number(r.documents)}</TableCell>
            <TableCell className="tabular-nums">{percent(Number(r.unknown), Number(r.documents), i18n.language)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
