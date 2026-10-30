import { useMemo } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { Search, X } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Badge, Button, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import {
  DataTable,
  PageHeader,
  Roles,
  StatusBadge,
  createDataTableColumnHelper,
  formatDate,
  formatDateTime,
  formatMoney,
  queryKeys,
  unwrap,
  useApi,
  useAuth,
  useCursorList,
  useTenantSwitcherOptional,
  type DataTableColumns,
} from "@docproc/app-shell";
import { documentStatuses, documentTypes, type DocumentStatus, type DocumentType } from "@/lib/documents";
import { LoadMore } from "@/components/LoadMore";

type DocumentSummary = components["schemas"]["DocumentSummary"];

const ALL = "__all__";
const PLATFORM = "__platform__";

const searchSchema = z.object({
  status: z.enum(documentStatuses as readonly [DocumentStatus, ...DocumentStatus[]]).optional().catch(undefined),
  type: z.enum(documentTypes as readonly [DocumentType, ...DocumentType[]]).optional().catch(undefined),
  q: z.string().optional().catch(undefined),
  flag: z.string().optional().catch(undefined),
  supplierId: z.string().uuid().optional().catch(undefined),
  from: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).optional().catch(undefined),
  to: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).optional().catch(undefined),
});
export type DocumentsSearch = z.infer<typeof searchSchema>;

export const Route = createFileRoute("/documents/")({ validateSearch: searchSchema, component: DocumentsPage });

const helper = createDataTableColumnHelper<DocumentSummary>();
const PAGE_SIZE = 50;

/** Cross-tenant document list (platform scope) with the portal's filters; the header's tenant switcher narrows it to one tenant. */
function DocumentsPage() {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  const { hasRole } = useAuth();
  const switcher = useTenantSwitcherOptional();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canSwitch = hasRole(Roles.SuperAdmin) && switcher !== null;
  const tenantId = switcher?.tenantId ?? null;

  const tenants = useQuery({
    queryKey: queryKeys.tenants.list({ limit: 200 }),
    queryFn: async () => unwrap(await client.GET("/api/v1/tenants", { params: { query: { limit: 200 } } })),
    enabled: canSwitch,
    staleTime: 60_000,
  });

  const query = useMemo(
    () => ({
      status: search.status,
      type: search.type,
      q: search.q?.trim() || undefined,
      flag: search.flag?.trim() || undefined,
      supplierId: search.supplierId,
      from: search.from,
      to: search.to,
    }),
    [search],
  );

  const documents = useCursorList<DocumentSummary>({
    queryKey: [...queryKeys.documents.list(query), tenantId],
    queryFn: async (cursor) => unwrap(await client.GET("/api/v1/documents", { params: { query: { ...query, limit: PAGE_SIZE, cursor: cursor ?? undefined } } })),
  });

  const set = (patch: Partial<DocumentsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });
  const hasFilters = Object.values(query).some((v) => v !== undefined);
  const tenantName = tenantId ? tenants.data?.items.find((x) => x.id === tenantId)?.name ?? tenantId : null;

  const columns = useMemo<DataTableColumns<DocumentSummary>>(
    () =>
      helper.columns([
        helper.accessor("documentNumber", {
          header: t("documents.number"),
          cell: (info) => (
            <Link to="/documents/$documentId" params={{ documentId: info.row.original.id }} className="font-medium underline-offset-4 hover:underline">
              {info.getValue() ?? <span className="font-mono text-xs">{info.row.original.id.slice(0, 8)}</span>}
            </Link>
          ),
        }),
        helper.accessor("status", { header: t("documents.status"), cell: (info) => <StatusBadge status={info.getValue()} /> }),
        helper.accessor("type", { header: t("documents.type"), cell: (info) => t(`documentType.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("supplierName", { header: t("documents.supplier"), cell: (info) => info.getValue() ?? "—" }),
        helper.accessor("issueDate", { header: t("documents.issueDate"), sortingFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDate(info.getValue(), i18n.language)}</span> }),
        helper.accessor("totalGross", { header: t("documents.total"), cell: (info) => <span className="tabular-nums">{formatMoney(info.getValue(), info.row.original.currency, i18n.language)}</span> }),
        helper.display({
          id: "flags",
          header: t("documents.flags"),
          cell: (info) => {
            const blocking = Number(info.row.original.blockingFlagCount);
            const warning = Number(info.row.original.warningFlagCount);
            if (!blocking && !warning) return <span className="text-muted-foreground">—</span>;
            return (
              <span className="flex gap-1">
                {blocking > 0 && <Badge variant="destructive">{t("documents.blocking", { count: blocking })}</Badge>}
                {warning > 0 && <Badge variant="secondary">{t("documents.warnings", { count: warning })}</Badge>}
              </span>
            );
          },
        }),
        helper.accessor("extractionMethod", { header: t("documents.method"), cell: (info) => (info.getValue() ? t(`common:sourceMethod.${info.getValue()}`, { defaultValue: info.getValue() ?? "" }) : "—") }),
        helper.accessor("pageCount", { header: t("documents.pages"), cell: (info) => <span className="tabular-nums">{String(info.getValue())}</span> }),
        helper.accessor("createdAt", { header: t("documents.receivedAt"), sortingFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDateTime(info.getValue(), i18n.language)}</span> }),
      ]),
    [t, i18n.language],
  );

  return (
    <>
      <PageHeader title={t("documents.title")} description={tenantName ? t("documents.descriptionTenant", { tenant: tenantName }) : t("documents.descriptionPlatform")} />
      <DataTable
        columns={columns}
        data={documents.items}
        getRowId={(row) => row.id}
        isLoading={documents.isLoading}
        caption={t("documents.caption")}
        emptyTitle={t("documents.empty")}
        emptyDescription={t("documents.emptyDescription")}
        initialColumnVisibility={{ extractionMethod: false, pageCount: false }}
        enableColumnVisibility
        toolbar={
          <>
            {canSwitch && switcher && (
              <Select value={tenantId ?? PLATFORM} onValueChange={(value) => switcher.setTenantId(value === PLATFORM ? null : value)}>
                <SelectTrigger className="w-52" aria-label={t("documents.tenantFilter")}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={PLATFORM}>{t("documents.allTenants")}</SelectItem>
                  {tenants.data?.items.map((tenant) => (
                    <SelectItem key={tenant.id} value={tenant.id}>
                      {tenant.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
            <Select value={search.status ?? ALL} onValueChange={(value) => set({ status: value === ALL ? undefined : (value as DocumentStatus) })}>
              <SelectTrigger className="w-44" aria-label={t("documents.status")}>
                <SelectValue placeholder={t("documents.status")} />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>{t("documents.allStatuses")}</SelectItem>
                {documentStatuses.map((status) => (
                  <SelectItem key={status} value={status}>
                    {t(`common:documentStatus.${status}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Select value={search.type ?? ALL} onValueChange={(value) => set({ type: value === ALL ? undefined : (value as DocumentType) })}>
              <SelectTrigger className="w-44" aria-label={t("documents.type")}>
                <SelectValue placeholder={t("documents.type")} />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>{t("documents.allTypes")}</SelectItem>
                {documentTypes.map((type) => (
                  <SelectItem key={type} value={type}>
                    {t(`documentType.${type}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <div className="relative">
              <Label htmlFor="document-search" className="sr-only">
                {t("documents.search")}
              </Label>
              <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
              <Input id="document-search" type="search" defaultValue={search.q ?? ""} onChange={(event) => set({ q: event.target.value || undefined })} placeholder={t("documents.search")} className="w-56 pl-8" />
            </div>
            <div>
              <Label htmlFor="document-flag" className="sr-only">
                {t("documents.flag")}
              </Label>
              <Input id="document-flag" defaultValue={search.flag ?? ""} onChange={(event) => set({ flag: event.target.value || undefined })} placeholder={t("documents.flag")} className="w-40 font-mono text-xs uppercase" />
            </div>
            <div className="flex items-center gap-1">
              <Label htmlFor="document-from" className="sr-only">
                {t("documents.from")}
              </Label>
              <Input id="document-from" type="date" value={search.from ?? ""} onChange={(event) => set({ from: event.target.value || undefined })} className="w-38" aria-label={t("documents.from")} />
              <span aria-hidden className="text-muted-foreground">
                –
              </span>
              <Label htmlFor="document-to" className="sr-only">
                {t("documents.to")}
              </Label>
              <Input id="document-to" type="date" value={search.to ?? ""} onChange={(event) => set({ to: event.target.value || undefined })} className="w-38" aria-label={t("documents.to")} />
            </div>
            {hasFilters && (
              <Button variant="ghost" size="sm" onClick={() => void navigate({ search: {}, replace: true })}>
                <X aria-hidden />
                {t("documents.clearFilters")}
              </Button>
            )}
          </>
        }
        footer={<LoadMore list={documents} />}
      />
    </>
  );
}
