import { useMemo } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { Search } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import { DataTable, PageHeader, Policies, TenantStatusBadge, createDataTableColumnHelper, formatDateTime, queryKeys, unwrap, useApi, useAuth, useCursorList, type DataTableColumns } from "@docproc/app-shell";
import { TenantFormDialog } from "@/components/TenantFormDialog";
import { LoadMore } from "@/components/LoadMore";

type Tenant = components["schemas"]["TenantResponse"];

export const tenantStatuses = ["Active", "Suspended", "Closed"] as const;
const ALL = "__all__";

const searchSchema = z.object({
  q: z.string().optional().catch(undefined),
  status: z.enum(tenantStatuses).optional().catch(undefined),
});

export const Route = createFileRoute("/tenants/")({ validateSearch: searchSchema, component: TenantsPage });

const helper = createDataTableColumnHelper<Tenant>();
const PAGE_SIZE = 50;

function TenantsPage() {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  const { hasPolicy } = useAuth();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const q = search.q?.trim() ? search.q.trim() : undefined;

  const tenants = useCursorList<Tenant>({
    queryKey: queryKeys.tenants.list({ q, status: search.status }),
    queryFn: async (cursor) => unwrap(await client.GET("/api/v1/tenants", { params: { query: { q, status: search.status, limit: PAGE_SIZE, cursor: cursor ?? undefined } } })),
  });

  const columns = useMemo<DataTableColumns<Tenant>>(
    () =>
      helper.columns([
        helper.accessor("name", {
          header: t("tenants.name"),
          cell: (info) => (
            <Link to="/tenants/$tenantId" params={{ tenantId: info.row.original.id }} className="font-medium underline-offset-4 hover:underline">
              {info.getValue()}
            </Link>
          ),
        }),
        helper.accessor("slug", { header: t("tenants.slug"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        helper.accessor("status", { header: t("tenants.status"), cell: (info) => <TenantStatusBadge status={info.getValue()} /> }),
        helper.accessor("legalName", { header: t("tenants.legalName") }),
        helper.accessor("countryCode", { header: t("tenants.country") }),
        helper.accessor("defaultCurrency", { header: t("tenants.currency") }),
        helper.accessor("createdAt", { header: t("tenants.createdAt"), sortingFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDateTime(info.getValue(), i18n.language)}</span> }),
      ]),
    [t, i18n.language],
  );

  return (
    <>
      <PageHeader title={t("tenants.title")} description={t("tenants.description")} actions={hasPolicy(Policies.PlatformAdmin) ? <TenantFormDialog /> : undefined} />
      <DataTable
        columns={columns}
        data={tenants.items}
        getRowId={(row) => row.id}
        isLoading={tenants.isLoading}
        caption={t("tenants.caption")}
        emptyTitle={t("tenants.empty")}
        emptyDescription={t("tenants.emptyDescription")}
        initialColumnVisibility={{ legalName: false }}
        enableColumnVisibility
        toolbar={
          <>
            <div className="relative">
              <Label htmlFor="tenant-search" className="sr-only">
                {t("tenants.search")}
              </Label>
              <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
              <Input
                id="tenant-search"
                type="search"
                defaultValue={search.q ?? ""}
                onChange={(event) => void navigate({ search: (prev) => ({ ...prev, q: event.target.value || undefined }), replace: true })}
                placeholder={t("tenants.search")}
                className="w-64 pl-8"
              />
            </div>
            <Select value={search.status ?? ALL} onValueChange={(value) => void navigate({ search: (prev) => ({ ...prev, status: value === ALL ? undefined : (value as (typeof tenantStatuses)[number]) }) })}>
              <SelectTrigger className="w-44" aria-label={t("tenants.status")}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>{t("tenants.allStatuses")}</SelectItem>
                {tenantStatuses.map((status) => (
                  <SelectItem key={status} value={status}>
                    {t(`common:tenantStatus.${status}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </>
        }
        footer={<LoadMore list={tenants} />}
      />
    </>
  );
}
