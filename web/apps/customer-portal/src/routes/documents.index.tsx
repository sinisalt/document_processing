import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { z } from "zod";
import { AlertTriangle, OctagonAlert, Search, X } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Badge, Button, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Tabs, TabsList, TabsTrigger } from "@docproc/ui";
import {
  DataTable,
  PageHeader,
  Policies,
  StatusBadge,
  createDataTableColumnHelper,
  formatDate,
  formatMoney,
  relativeTime,
  useAuth,
  type DocumentListQuery,
} from "@docproc/app-shell";
import { UploadDialog } from "@/components/UploadDialog";
import { inboxTabs, statusFilterOf, type InboxTab } from "@/lib/inbox";
import { EMPTY_ARRAY, useDocumentList, useSuppliers, type DocumentSummary } from "@/lib/queries";

type DocumentType = components["schemas"]["DocumentType"];
const documentTypes: DocumentType[] = ["Invoice", "CreditNote", "PurchaseOrder", "DeliveryNote", "Statement", "Reminder", "Other", "Unknown"];

const ALL = "__all__";

const searchSchema = z.object({
  tab: z.enum(inboxTabs).optional().catch(undefined),
  type: z.string().optional().catch(undefined),
  supplierId: z.string().optional().catch(undefined),
  from: z.string().optional().catch(undefined),
  to: z.string().optional().catch(undefined),
  q: z.string().optional().catch(undefined),
});

export type InboxSearch = z.infer<typeof searchSchema>;

export const Route = createFileRoute("/documents/")({
  validateSearch: searchSchema,
  component: InboxPage,
});

const helper = createDataTableColumnHelper<DocumentSummary>();

function InboxPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate({ from: Route.fullPath });
  const search = Route.useSearch();
  const { hasPolicy } = useAuth();
  const tab: InboxTab = search.tab ?? "needsReview";
  const [draftQ, setDraftQ] = useState(search.q ?? "");

  const query = useMemo<DocumentListQuery>(
    () => ({
      status: statusFilterOf(tab),
      type: search.type || undefined,
      supplierId: search.supplierId || undefined,
      from: search.from || undefined,
      to: search.to || undefined,
      q: search.q || undefined,
      limit: 50,
    }),
    [tab, search.type, search.supplierId, search.from, search.to, search.q],
  );

  const list = useDocumentList(query);
  const suppliers = useSuppliers({ limit: 200 });

  const setSearch = (patch: Partial<InboxSearch>) =>
    void navigate({ search: (previous) => ({ ...previous, ...patch }) });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("documentNumber", {
          header: t("inbox.columns.documentNumber"),
          cell: (info) => <span className="font-medium">{info.getValue() ?? t("inbox.noNumber")}</span>,
        }),
        helper.accessor("supplierName", { header: t("inbox.columns.supplier"), cell: (info) => info.getValue() ?? <span className="text-muted-foreground">—</span> }),
        helper.accessor("type", { header: t("inbox.columns.type"), cell: (info) => t(`documentType.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("issueDate", { header: t("inbox.columns.issueDate"), cell: (info) => formatDate(info.getValue(), i18n.language), sortFn: "datetime" }),
        helper.accessor("totalGross", {
          header: t("inbox.columns.total"),
          cell: (info) => <span className="tabular-nums">{formatMoney(info.getValue(), info.row.original.currency, i18n.language)}</span>,
          sortFn: "basic",
        }),
        helper.accessor("status", { header: t("inbox.columns.status"), cell: (info) => <StatusBadge status={info.getValue()} /> }),
        helper.display({
          id: "flags",
          header: t("inbox.columns.flags"),
          cell: ({ row }) => <FlagCounts blocking={Number(row.original.blockingFlagCount)} warning={Number(row.original.warningFlagCount)} />,
        }),
        helper.accessor("createdAt", {
          header: t("inbox.columns.received"),
          cell: (info) => (
            <time dateTime={info.getValue()} title={info.getValue()}>
              {relativeTime(info.getValue(), i18n.language)}
            </time>
          ),
          sortFn: "datetime",
        }),
      ]),
    [t, i18n.language],
  );

  const hasFilters = !!(search.type || search.supplierId || search.from || search.to || search.q);

  return (
    <>
      <PageHeader title={t("inbox.title")} description={t("inbox.description")} actions={hasPolicy(Policies.Ingest) ? <UploadDialog /> : undefined} />

      <Tabs value={tab} onValueChange={(value) => setSearch({ tab: value as InboxTab })}>
        <TabsList aria-label={t("inbox.tabsLabel")} className="flex-wrap">
          {inboxTabs.map((key) => (
            <TabsTrigger key={key} value={key}>
              {t(`inbox.tabs.${key}`)}
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>

      <DataTable
        columns={columns}
        data={list.items.length ? list.items : EMPTY_ARRAY}
        getRowId={(row) => row.id}
        isLoading={list.isLoading}
        enableColumnVisibility
        caption={t("inbox.caption")}
        onRowClick={(row) => void navigate({ to: "/documents/$documentId", params: { documentId: row.id } })}
        emptyTitle={t("inbox.emptyTitle")}
        emptyDescription={hasFilters ? t("inbox.emptyFiltered") : t(`inbox.empty.${tab}`)}
        toolbar={
          <form
            className="flex flex-wrap items-end gap-2"
            role="search"
            onSubmit={(event) => {
              event.preventDefault();
              setSearch({ q: draftQ.trim() || undefined });
            }}
          >
            <div className="flex flex-col gap-1">
              <Label htmlFor="inbox-q" className="text-xs text-muted-foreground">
                {t("inbox.filters.search")}
              </Label>
              <div className="relative">
                <Search className="pointer-events-none absolute top-1/2 left-2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
                <Input id="inbox-q" value={draftQ} onChange={(event) => setDraftQ(event.target.value)} placeholder={t("inbox.filters.searchPlaceholder")} className="w-56 pl-8" />
              </div>
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="inbox-type" className="text-xs text-muted-foreground">
                {t("inbox.filters.type")}
              </Label>
              <Select value={search.type ?? ALL} onValueChange={(value) => setSearch({ type: value === ALL ? undefined : value })}>
                <SelectTrigger id="inbox-type" className="w-40" aria-label={t("inbox.filters.type")}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL}>{t("inbox.filters.anyType")}</SelectItem>
                  {documentTypes.map((type) => (
                    <SelectItem key={type} value={type}>
                      {t(`documentType.${type}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="inbox-supplier" className="text-xs text-muted-foreground">
                {t("inbox.filters.supplier")}
              </Label>
              <Select value={search.supplierId ?? ALL} onValueChange={(value) => setSearch({ supplierId: value === ALL ? undefined : value })}>
                <SelectTrigger id="inbox-supplier" className="w-48" aria-label={t("inbox.filters.supplier")}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL}>{t("inbox.filters.anySupplier")}</SelectItem>
                  {suppliers.items.map((supplier) => (
                    <SelectItem key={supplier.id} value={supplier.id}>
                      {supplier.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="inbox-from" className="text-xs text-muted-foreground">
                {t("inbox.filters.from")}
              </Label>
              <Input id="inbox-from" type="date" value={search.from ?? ""} onChange={(event) => setSearch({ from: event.target.value || undefined })} className="w-40" />
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="inbox-to" className="text-xs text-muted-foreground">
                {t("inbox.filters.to")}
              </Label>
              <Input id="inbox-to" type="date" value={search.to ?? ""} onChange={(event) => setSearch({ to: event.target.value || undefined })} className="w-40" />
            </div>
            <Button type="submit" variant="secondary" size="sm">
              {t("inbox.filters.apply")}
            </Button>
            {hasFilters && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => {
                  setDraftQ("");
                  setSearch({ type: undefined, supplierId: undefined, from: undefined, to: undefined, q: undefined });
                }}
              >
                <X aria-hidden />
                {t("inbox.filters.clear")}
              </Button>
            )}
          </form>
        }
        footer={
          <>
            <span aria-live="polite">{t("common:table.rowCount", { count: list.items.length })}</span>
            {list.hasNextPage && (
              <Button variant="outline" size="sm" onClick={() => void list.fetchNextPage()} disabled={list.isFetchingNextPage} aria-busy={list.isFetchingNextPage}>
                {t("common:table.loadMore")}
              </Button>
            )}
          </>
        }
      />
    </>
  );
}

function FlagCounts({ blocking, warning }: { blocking: number; warning: number }) {
  const { t } = useTranslation();
  if (blocking === 0 && warning === 0) return <span className="text-muted-foreground">—</span>;
  return (
    <span className="flex items-center gap-1">
      {blocking > 0 && (
        <Badge variant="destructive" aria-label={t("inbox.blockingFlags", { count: blocking })}>
          <OctagonAlert aria-hidden />
          {blocking}
        </Badge>
      )}
      {warning > 0 && (
        <Badge variant="outline" aria-label={t("inbox.warningFlags", { count: warning })}>
          <AlertTriangle aria-hidden />
          {warning}
        </Badge>
      )}
    </span>
  );
}
