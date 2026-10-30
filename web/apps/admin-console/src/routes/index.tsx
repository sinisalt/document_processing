import { useMemo, useState } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useQueries, useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { AlertTriangle, Building2, ClipboardList, Coins, RotateCcw } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@docproc/ui";
import { DataTable, Policies, StatusBadge, createDataTableColumnHelper, formatDateTime, formatNumber, queryKeys, unwrap, useApi, useAuth, useTenantSwitcherOptional, type DataTableColumns } from "@docproc/app-shell";
import { documentStatuses, pageCountLabel } from "@/lib/documents";
import { formatUsd, monthRange, usageRows } from "@/lib/usage";
import { ReprocessDialog } from "@/components/DocumentActions";

export const Route = createFileRoute("/")({ component: DashboardPage });

type DocumentSummary = components["schemas"]["DocumentSummary"];

const PAGE = 200;
const FAILED_PAGE = 25;
const helper = createDataTableColumnHelper<DocumentSummary>();
const EMPTY: DocumentSummary[] = [];

type StatLink = "/tenants" | "/review" | "/documents" | "/llm";

function StatCard({ title, value, hint, icon: Icon, to }: { title: string; value: React.ReactNode; hint?: React.ReactNode; icon: React.ComponentType<{ className?: string }>; to?: StatLink }) {
  const card = (
    <Card className={to ? "h-full transition-colors hover:bg-accent/40" : undefined}>
      <CardHeader className="flex flex-row items-center justify-between gap-2 pb-2">
        <CardTitle className="text-sm font-medium text-muted-foreground">{title}</CardTitle>
        <Icon className="size-4 text-muted-foreground" aria-hidden />
      </CardHeader>
      <CardContent>
        <div className="text-3xl font-semibold tabular-nums">{value}</div>
        {hint && <p className="mt-1 text-xs text-muted-foreground">{hint}</p>}
      </CardContent>
    </Card>
  );
  return to ? (
    <Link to={to} className="block rounded-xl focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50" aria-label={title}>
      {card}
    </Link>
  ) : (
    card
  );
}

function DashboardPage() {
  const { t, i18n } = useTranslation("admin");
  const { user, hasPolicy } = useAuth();
  const { client } = useApi();
  const switcher = useTenantSwitcherOptional();
  const tenantId = switcher?.tenantId ?? null;
  const isPlatformAdmin = hasPolicy(Policies.PlatformAdmin);
  const [retrying, setRetrying] = useState<DocumentSummary | null>(null);

  const tenants = useQuery({
    queryKey: queryKeys.tenants.list({ limit: PAGE }),
    queryFn: async () => unwrap(await client.GET("/api/v1/tenants", { params: { query: { limit: PAGE } } })),
  });

  const byStatus = useQueries({
    queries: documentStatuses.map((status) => ({
      queryKey: [...queryKeys.documents.list({ status }), "count", tenantId],
      queryFn: async () => unwrap(await client.GET("/api/v1/documents", { params: { query: { status, limit: PAGE } } })),
      staleTime: 30_000,
    })),
  });

  const backlog = useQuery({
    queryKey: [...queryKeys.reviewTasks.list({}), "count", tenantId],
    queryFn: async () => unwrap(await client.GET("/api/v1/review-tasks", { params: { query: { limit: PAGE } } })),
  });

  const month = useMemo(() => monthRange(), []);
  const spend = useQuery({
    queryKey: queryKeys.llm.platformUsage({ ...month, groupBy: "tenant" }),
    queryFn: async () => unwrap(await client.GET("/api/v1/llm/usage", { params: { query: { ...month, groupBy: "tenant" } } })),
    enabled: isPlatformAdmin,
  });

  const failed = useQuery({
    queryKey: [...queryKeys.documents.list({ status: "Failed" }), "dashboard", tenantId],
    queryFn: async () => unwrap(await client.GET("/api/v1/documents", { params: { query: { status: "Failed", limit: FAILED_PAGE } } })),
  });

  const tenantName = useMemo(() => {
    const map = new Map((tenants.data?.items ?? []).map((tenant) => [tenant.id, tenant.name] as const));
    return (id: string) => map.get(id);
  }, [tenants.data]);
  const spendRows = useMemo(() => (spend.data ? usageRows(spend.data, tenantName).slice(0, 5) : []), [spend.data, tenantName]);
  const selectedTenant = tenantId ? tenantName(tenantId) ?? tenantId : null;

  const failedColumns = useMemo<DataTableColumns<DocumentSummary>>(
    () =>
      helper.columns([
        helper.accessor("documentNumber", {
          header: t("documents.number"),
          cell: (info) => (
            <Link to="/documents/$documentId" params={{ documentId: info.row.original.id }} className="font-medium underline-offset-4 hover:underline">
              {info.getValue() ?? info.row.original.id.slice(0, 8)}
            </Link>
          ),
        }),
        helper.accessor("supplierName", { header: t("documents.supplier"), cell: (info) => info.getValue() ?? "—" }),
        helper.accessor("type", { header: t("documents.type"), cell: (info) => t(`documentType.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("updatedAt", { header: t("documents.updatedAt"), sortingFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDateTime(info.getValue(), i18n.language)}</span> }),
        helper.display({
          id: "actions",
          header: () => <span className="sr-only">{t("dashboard.actions")}</span>,
          cell: (info) => (
            <div className="flex justify-end gap-1">
              <Button variant="outline" size="sm" onClick={() => setRetrying(info.row.original)} aria-label={t("dashboard.retryAria", { document: info.row.original.documentNumber ?? info.row.original.id })}>
                <RotateCcw aria-hidden />
                {t("dashboard.retry")}
              </Button>
              <Button variant="ghost" size="sm" asChild>
                <Link to="/documents/$documentId" params={{ documentId: info.row.original.id }}>
                  {t("dashboard.inspect")}
                </Link>
              </Button>
            </div>
          ),
        }),
      ]),
    [t, i18n.language],
  );

  return (
    <>
      <div className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">{t("dashboard.title")}</h1>
        <p className="text-sm text-muted-foreground">{t("dashboard.welcome", { name: user?.displayName })}</p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard
          title={t("dashboard.tenants")}
          value={tenants.data ? pageCountLabel(tenants.data.items.length, tenants.data.nextCursor, PAGE) : <Skeleton className="h-9 w-16" />}
          hint={tenants.data ? t("dashboard.tenantsHint", { active: tenants.data.items.filter((x) => x.status === "Active").length, suspended: tenants.data.items.filter((x) => x.status === "Suspended").length }) : undefined}
          icon={Building2}
          to="/tenants"
        />
        <StatCard
          title={t("dashboard.backlog")}
          value={backlog.data ? pageCountLabel(backlog.data.items.length, backlog.data.nextCursor, PAGE) : <Skeleton className="h-9 w-16" />}
          hint={selectedTenant ? t("dashboard.inTenant", { tenant: selectedTenant }) : t("dashboard.acrossTenants")}
          icon={ClipboardList}
          to="/review"
        />
        <StatCard
          title={t("dashboard.failed")}
          value={failed.data ? pageCountLabel(failed.data.items.length, failed.data.nextCursor, FAILED_PAGE) : <Skeleton className="h-9 w-16" />}
          hint={selectedTenant ? t("dashboard.inTenant", { tenant: selectedTenant }) : t("dashboard.acrossTenants")}
          icon={AlertTriangle}
          to="/documents"
        />
        {isPlatformAdmin ? (
          <StatCard
            title={t("dashboard.spend")}
            value={spend.data ? formatUsd(spend.data.total.estimatedCostUsd, i18n.language) : spend.isError ? "—" : <Skeleton className="h-9 w-24" />}
            hint={spend.data ? t("dashboard.spendHint", { calls: formatNumber(spend.data.total.calls, i18n.language), from: spend.data.from }) : undefined}
            icon={Coins}
            to="/llm"
          />
        ) : (
          <StatCard title={t("dashboard.spend")} value="—" hint={t("dashboard.spendNeedsPlatformAdmin")} icon={Coins} />
        )}
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader>
            <CardTitle>{t("dashboard.byStatus")}</CardTitle>
            <CardDescription>{selectedTenant ? t("dashboard.byStatusTenant", { tenant: selectedTenant }) : t("dashboard.byStatusPlatform")}</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4" aria-label={t("dashboard.byStatus")}>
              {documentStatuses.map((status, index) => {
                const result = byStatus[index]!;
                return (
                  <li key={status}>
                    <Link to="/documents" search={{ status }} className="flex flex-col gap-1 rounded-md border p-3 transition-colors hover:bg-accent/40" data-status={status}>
                      <StatusBadge status={status} className="w-fit" />
                      <span className="text-2xl font-semibold tabular-nums">{result.data ? pageCountLabel(result.data.items.length, result.data.nextCursor, PAGE) : result.isError ? "—" : <Skeleton className="h-8 w-10" />}</span>
                    </Link>
                  </li>
                );
              })}
            </ul>
            <p className="mt-3 text-xs text-muted-foreground">{t("dashboard.countHint", { limit: PAGE })}</p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("dashboard.spendByTenant")}</CardTitle>
            <CardDescription>{t("dashboard.spendByTenantDescription", { from: month.from, to: month.to })}</CardDescription>
          </CardHeader>
          <CardContent>
            {!isPlatformAdmin ? (
              <p className="text-sm text-muted-foreground">{t("dashboard.spendNeedsPlatformAdmin")}</p>
            ) : spend.isPending ? (
              <Skeleton className="h-24 w-full" />
            ) : spendRows.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t("dashboard.noSpend")}</p>
            ) : (
              <ol className="grid gap-2">
                {spendRows.map((row) => (
                  <li key={row.key} className="flex items-center justify-between gap-2 text-sm">
                    <span className={row.label === row.key ? "font-mono text-xs" : undefined}>{row.label}</span>
                    <span className="flex items-center gap-2 tabular-nums">
                      <Badge variant="outline">{t("usage.callsCount", { count: row.calls })}</Badge>
                      {formatUsd(row.costUsd, i18n.language)}
                    </span>
                  </li>
                ))}
              </ol>
            )}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t("dashboard.failedDocuments")}</CardTitle>
          <CardDescription>{t("dashboard.failedDocumentsDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          <DataTable
            columns={failedColumns}
            data={failed.data?.items ?? EMPTY}
            getRowId={(row) => row.id}
            isLoading={failed.isPending}
            caption={t("dashboard.failedDocuments")}
            emptyTitle={t("dashboard.noFailed")}
            emptyDescription={t("dashboard.noFailedDescription")}
            footer={
              failed.data?.nextCursor ? (
                <Button variant="link" asChild>
                  <Link to="/documents" search={{ status: "Failed" }}>
                    {t("dashboard.allFailed")}
                  </Link>
                </Button>
              ) : undefined
            }
          />
        </CardContent>
      </Card>

      {retrying && (
        <ReprocessDialog documentId={retrying.id} documentLabel={retrying.documentNumber ?? retrying.id.slice(0, 8)} open onOpenChange={(open) => !open && setRetrying(null)} />
      )}
    </>
  );
}
