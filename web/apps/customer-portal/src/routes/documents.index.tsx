import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { z } from "zod";
import { AlertTriangle, OctagonAlert, Search, X } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Badge, Button, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Tabs, TabsList, TabsTrigger, Tooltip, TooltipContent, TooltipTrigger, useIsMobile } from "@docproc/ui";
import {
  DataTable,
  LocalizedDateInput,
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
  const { hasPolicy, user } = useAuth();
  // Suppliers belong to a tenant; a platform account would get 400 "Tenant required", so it gets no supplier filter.
  const tenantScope = user?.scope === "tenant";
  const tab: InboxTab = search.tab ?? "needsReview";
  // A phone has room for two columns: the document (number, supplier, date) and its state (status, total).
  const phone = useIsMobile();
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

  // The API answers from > to with 400; such a range is shown as an error at the inputs and not sent.
  const rangeInvalid = !!(search.from && search.to && search.from > search.to);
  const list = useDocumentList(query, !rangeInvalid);
  const suppliers = useSuppliers({ limit: 200 }, tenantScope);

  const setSearch = (patch: Partial<InboxSearch>) =>
    void navigate({ search: (previous) => ({ ...previous, ...patch }) });
  /** A date filter changes once the text is a date or empty; a date half typed leaves the URL alone. */
  const setDate = (key: "from" | "to", value: string) => {
    if (value === "") setSearch({ [key]: undefined });
    else if (/^\d{4}-\d{2}-\d{2}$/.test(value)) setSearch({ [key]: value });
  };

  const phoneColumns = useMemo(
    () =>
      helper.columns([
        helper.accessor("documentNumber", {
          header: t("inbox.columns.document"),
          cell: ({ row }) => (
            // Bounded, so a long supplier name truncates instead of pushing the status column off the screen.
            <div className="flex max-w-48 min-w-0 flex-col gap-0.5">
              <span className="truncate font-medium">{row.original.documentNumber ?? t("inbox.noNumber")}</span>
              <span className="truncate text-xs text-muted-foreground">{row.original.supplierName ?? "—"}</span>
              {row.original.issueDate && <span className="text-xs text-muted-foreground">{formatDate(row.original.issueDate, i18n.language)}</span>}
            </div>
          ),
        }),
        helper.accessor("status", {
          header: t("inbox.columns.status"),
          // The badges and amounts sit on the right edge; the header goes with them.
          meta: { align: "end" },
          cell: ({ row }) => (
            <div className="flex flex-col items-end gap-1">
              <StatusBadge status={row.original.status} />
              <span className="text-xs tabular-nums">{formatMoney(row.original.totalGross, row.original.currency, i18n.language)}</span>
            </div>
          ),
        }),
      ]),
    [t, i18n.language],
  );

  const desktopColumns = useMemo(
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
  const columns = phone ? phoneColumns : desktopColumns;

  const hasFilters = !!(search.type || search.supplierId || search.from || search.to || search.q);

  return (
    <>
      {/* Uploads belong to a tenant: in platform scope POST /ingestions answers 400 (docs/05), so no Upload there. */}
      <PageHeader title={t("inbox.title")} description={t("inbox.description")} actions={tenantScope && hasPolicy(Policies.Ingest) ? <UploadDialog /> : undefined} />

      <Tabs value={tab} onValueChange={(value) => setSearch({ tab: value as InboxTab })}>
        <TabsList aria-label={t("inbox.tabsLabel")} className="max-w-full flex-wrap">
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
        isLoading={list.isLoading && !rangeInvalid}
        query={list}
        enableColumnVisibility={!phone}
        caption={t("inbox.caption")}
        onRowClick={(row) => void navigate({ to: "/documents/$documentId", params: { documentId: row.id } })}
        emptyTitle={rangeInvalid ? t("inbox.filters.rangeInvalidTitle") : t("inbox.emptyTitle")}
        emptyDescription={rangeInvalid ? t("inbox.filters.rangeInvalid") : hasFilters ? t("inbox.emptyFiltered") : t(`inbox.empty.${tab}`)}
        toolbar={
          <form
            // A phone stacks the filters two to a row at full width; from sm up they share one row, the search box taking
            // the room that is left, so the row fits "Clear filters" (and the Columns button beside it) without wrapping.
            className="grid w-full grid-cols-2 items-end gap-2 sm:flex sm:flex-wrap"
            role="search"
            onSubmit={(event) => {
              event.preventDefault();
              setSearch({ q: draftQ.trim() || undefined });
            }}
          >
            {/* At least 15rem, growing into the free room: "Number, supplier, file name" fits beside the icon (w-56 cut it). */}
            <div className="col-span-2 flex flex-col gap-1 sm:max-w-80 sm:min-w-60 sm:flex-1">
              <Label htmlFor="inbox-q" className="text-xs text-muted-foreground">
                {t("inbox.filters.search")}
              </Label>
              <div className="relative">
                <Search className="pointer-events-none absolute top-1/2 left-2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
                <Input id="inbox-q" value={draftQ} onChange={(event) => setDraftQ(event.target.value)} placeholder={t("inbox.filters.searchPlaceholder")} className="w-full pl-8" />
              </div>
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="inbox-type" className="text-xs text-muted-foreground">
                {t("inbox.filters.type")}
              </Label>
              <Select value={search.type ?? ALL} onValueChange={(value) => setSearch({ type: value === ALL ? undefined : value })}>
                <SelectTrigger id="inbox-type" className="w-full sm:w-36" aria-label={t("inbox.filters.type")}>
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
            {tenantScope && (
              <div className="flex flex-col gap-1">
                <Label htmlFor="inbox-supplier" className="text-xs text-muted-foreground">
                  {t("inbox.filters.supplier")}
                </Label>
                <Select value={search.supplierId ?? ALL} onValueChange={(value) => setSearch({ supplierId: value === ALL ? undefined : value })}>
                  <SelectTrigger id="inbox-supplier" className="w-full sm:w-44" aria-label={t("inbox.filters.supplier")}>
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
            )}
            <div className="flex flex-col gap-1">
              <Label htmlFor="inbox-from" className="text-xs text-muted-foreground">
                {t("inbox.filters.from")}
              </Label>
              <LocalizedDateInput id="inbox-from" value={search.from ?? ""} onValueChange={(value) => setDate("from", value)} max={search.to || undefined} className="w-full sm:w-36" />
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="inbox-to" className="text-xs text-muted-foreground">
                {t("inbox.filters.to")}
              </Label>
              <LocalizedDateInput
                id="inbox-to"
                value={search.to ?? ""}
                min={search.from || undefined}
                onValueChange={(value) => setDate("to", value)}
                className="w-full sm:w-36"
                aria-invalid={rangeInvalid || undefined}
                aria-describedby={rangeInvalid ? "inbox-range-error" : undefined}
              />
              {rangeInvalid && (
                <p id="inbox-range-error" role="alert" className="text-xs text-destructive">
                  {t("inbox.filters.rangeInvalid")}
                </p>
              )}
            </div>
            <Button type="submit" variant="secondary">
              {t("inbox.filters.apply")}
            </Button>
            {hasFilters && (
              // From sm up an icon button with a tooltip, so it stays on the row of filters instead of wrapping onto
              // a row of its own; a phone has the room for its text beside Apply.
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button
                    type="button"
                    variant="ghost"
                    className="sm:w-9 sm:px-0"
                    data-testid="inbox-clear-filters"
                    onClick={() => {
                      setDraftQ("");
                      setSearch({ type: undefined, supplierId: undefined, from: undefined, to: undefined, q: undefined });
                    }}
                  >
                    <X aria-hidden />
                    <span className="sm:sr-only">{t("inbox.filters.clear")}</span>
                  </Button>
                </TooltipTrigger>
                <TooltipContent className="max-sm:hidden">{t("inbox.filters.clear")}</TooltipContent>
              </Tooltip>
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
