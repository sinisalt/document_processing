import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, createFileRoute, useNavigate } from "@tanstack/react-router";
import { z } from "zod";
import { AlertTriangle, OctagonAlert, Search, X } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Badge, Button, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Tabs, TabsList, TabsTrigger, Toggle, Tooltip, TooltipContent, TooltipTrigger, useIsMobile } from "@docproc/ui";
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
import { useDocumentTypes, useTenantFeatures } from "@docproc/review";
import { UploadDialog } from "@/components/UploadDialog";
import { MAX_FIELD_COLUMNS, fieldFilterQuery, inboxTabs, indexedFieldOptions, statusFilterOf, typeLabel, typeOptions, type IndexedFieldOption, type InboxTab } from "@/lib/inbox";
import { documentSortOf, dueState } from "@/lib/priority";
import { EMPTY_ARRAY, useDocumentList, useSuppliers, type DocumentSummary } from "@/lib/queries";

const ALL = "__all__";

const searchSchema = z.object({
  tab: z.enum(inboxTabs).optional().catch(undefined),
  type: z.string().optional().catch(undefined),
  supplierId: z.string().optional().catch(undefined),
  from: z.string().optional().catch(undefined),
  to: z.string().optional().catch(undefined),
  q: z.string().optional().catch(undefined),
  /** An indexed field's definition path and the value to find in it (`?field.<path>=value`, docs/11 §18). */
  field: z.string().optional().catch(undefined),
  value: z.string().optional().catch(undefined),
  /** Documents held with NEW_LAYOUT (docs/11 §12.10 step 1), and those waiting for one pending Candidate layout. */
  flag: z.enum(["NEW_LAYOUT"]).optional().catch(undefined),
  pendingLayoutVersionId: z.string().optional().catch(undefined),
  /** `reviewDue`: urgent reviews first, then by their due time (docs/11 §19 item 7, daily specials). */
  sort: z.enum(["reviewDue"]).optional().catch(undefined),
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
  const [draftValue, setDraftValue] = useState(search.value ?? "");

  // The types and their indexed fields come from the catalogue (docs/11 §19); a platform account has none.
  const types = useDocumentTypes(tenantScope);
  const typeChoices = useMemo(() => typeOptions(types.data, i18n.language), [types.data, i18n.language]);
  const fieldChoices = useMemo(() => indexedFieldOptions(types.data, search.type || undefined, i18n.language), [types.data, search.type, i18n.language]);
  // Columns for the indexed fields of the type the inbox is filtered by.
  const fieldColumns = useMemo(() => (search.type ? fieldChoices.slice(0, MAX_FIELD_COLUMNS) : []), [search.type, fieldChoices]);
  const filterField = fieldChoices.find((option) => option.path === search.field);
  // The "New layouts" filter is offered to tenants with supplierLayouts (principle 6); others see the inbox as before.
  const features = useTenantFeatures();
  const layoutsOn = tenantScope && features.data?.supplierLayouts === true;
  // A review due time comes from a type's `review.dueBy` (docs/11 §5.8), so "Urgent first" and the "Review due" column
  // are offered with configurableTypes; a tenant without it sees the inbox as before (an address that sorts still does).
  const dueOn = tenantScope && features.data?.configurableTypes === true;
  const dueSort = search.sort === "reviewDue";
  const newLayoutFilter = search.flag === "NEW_LAYOUT" || !!search.pendingLayoutVersionId;

  const query = useMemo<DocumentListQuery>(
    () => ({
      status: statusFilterOf(tab),
      type: search.type || undefined,
      supplierId: search.supplierId || undefined,
      from: search.from || undefined,
      to: search.to || undefined,
      q: search.q || undefined,
      fields: fieldColumns.length > 0 ? fieldColumns.map((column) => column.path).join(",") : undefined,
      // `field.<path>` is read from the query string by the API, so it is not part of the generated parameters.
      ...(fieldFilterQuery(search.field, search.value) as Partial<DocumentListQuery>),
      flag: search.flag || undefined,
      pendingLayoutVersionId: search.pendingLayoutVersionId || undefined,
      // "Urgent first" is the server's order (priority, then due time), so it holds across every page.
      sort: documentSortOf(search.sort),
      limit: 50,
    }),
    [tab, search.type, search.supplierId, search.from, search.to, search.q, search.field, search.value, fieldColumns, search.flag, search.pendingLayoutVersionId, search.sort],
  );

  // The API answers from > to with 400; such a range is shown as an error at the inputs and not sent.
  const rangeInvalid = !!(search.from && search.to && search.from > search.to);
  const list = useDocumentList(query, !rangeInvalid);
  const suppliers = useSuppliers({ limit: 200 }, tenantScope);
  const rows = list.items;

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
              <span className="flex items-center gap-1">
                <PriorityBadge priority={row.original.reviewPriority} />
                <StatusBadge status={row.original.status} />
              </span>
              <span className="text-xs tabular-nums">
                <TotalOrValidity document={row.original} />
              </span>
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
          cell: (info) => (
            <span className="flex items-center gap-2">
              <span className="font-medium">{info.getValue() ?? t("inbox.noNumber")}</span>
              <SupersededLink document={info.row.original} />
              {newLayoutFilter && (
                <Badge variant="warning" data-testid="inbox-new-layout-badge">
                  {t("inbox.newLayoutBadge")}
                </Badge>
              )}
            </span>
          ),
        }),
        helper.accessor("supplierName", { header: t("inbox.columns.supplier"), cell: (info) => info.getValue() ?? <span className="text-muted-foreground">—</span> }),
        // A legacy type by its translation, any other (a price list typed in review) by its catalogue name.
        helper.accessor("type", { header: t("inbox.columns.type"), cell: (info) => typeLabel(info.getValue(), typeChoices) ?? t(`documentType.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("issueDate", { header: t("inbox.columns.issueDate"), cell: (info) => formatDate(info.getValue(), i18n.language), sortFn: "datetime" }),
        // The roles of docs/11 §6.2, hidden until picked under Columns (a price list also shows them in place of a total).
        helper.accessor("validFrom", { header: t("inbox.columns.validFrom"), cell: (info) => formatDate(info.getValue() ?? null, i18n.language), sortFn: "datetime" }),
        helper.accessor("validUntil", { header: t("inbox.columns.validUntil"), cell: (info) => formatDate(info.getValue() ?? null, i18n.language), sortFn: "datetime" }),
        helper.accessor("lineCount", {
          header: t("inbox.columns.lineCount"),
          cell: (info) => <span className="tabular-nums">{info.getValue() === null || info.getValue() === undefined ? "—" : String(info.getValue())}</span>,
          sortFn: "basic",
        }),
        ...fieldColumns.map((column) =>
          helper.display({
            id: `field:${column.path}`,
            header: column.name,
            cell: ({ row }) => <IndexedValue field={column} value={row.original.fields?.[column.path]} />,
          }),
        ),
        helper.accessor("totalGross", {
          header: t("inbox.columns.total"),
          cell: (info) => (
            <span className="tabular-nums">
              <TotalOrValidity document={info.row.original} />
            </span>
          ),
          sortFn: "basic",
        }),
        helper.accessor("status", {
          header: t("inbox.columns.status"),
          cell: (info) => (
            <span className="flex items-center gap-1">
              <StatusBadge status={info.getValue()} />
              <PriorityBadge priority={info.row.original.reviewPriority} />
            </span>
          ),
        }),
        helper.display({
          id: "flags",
          header: t("inbox.columns.flags"),
          cell: ({ row }) => <FlagCounts blocking={Number(row.original.blockingFlagCount)} warning={Number(row.original.warningFlagCount)} />,
        }),
        ...(dueOn || dueSort
          ? [
              helper.accessor("reviewDueAt", {
                header: t("inbox.columns.reviewDue"),
                cell: (info) => <ReviewDue dueAt={info.getValue() ?? null} />,
                sortFn: "datetime",
              }),
            ]
          : []),
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
    [t, i18n.language, fieldColumns, typeChoices, newLayoutFilter, dueOn, dueSort],
  );
  const columns = phone ? phoneColumns : desktopColumns;

  const hasFilters = !!(search.type || search.supplierId || search.from || search.to || search.q || search.field || search.flag || search.pendingLayoutVersionId);

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
        data={rows.length ? rows : EMPTY_ARRAY}
        getRowId={(row) => row.id}
        isLoading={list.isLoading && !rangeInvalid}
        query={list}
        enableColumnVisibility={!phone}
        initialColumnVisibility={{ validFrom: false, validUntil: false, lineCount: false }}
        // A list another list replaced is greyed out (docs/11 §19 item 7); its number links to the replacing one.
        rowClassName={(row) => (row.supersededBy ? "opacity-60" : undefined)}
        rowAttributes={(row) => ({ "data-superseded": row.supersededBy ? "true" : undefined, "data-priority": row.reviewPriority ?? undefined })}
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
              setSearch({ q: draftQ.trim() || undefined, value: search.field ? draftValue.trim() || undefined : undefined });
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
              <Select
                value={search.type ?? ALL}
                onValueChange={(value) => {
                  const type = value === ALL ? undefined : value;
                  // A field filter of another type's field would find nothing: it goes with the type.
                  const keepsField = !!search.field && indexedFieldOptions(types.data, type, i18n.language).some((option) => option.path === search.field);
                  setSearch({ type, ...(keepsField ? {} : { field: undefined, value: undefined }) });
                  if (!keepsField) setDraftValue("");
                }}
              >
                <SelectTrigger id="inbox-type" className="w-full sm:w-36" aria-label={t("inbox.filters.type")}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL}>{t("inbox.filters.anyType")}</SelectItem>
                  {typeChoices.map((option) => (
                    <SelectItem key={option.key} value={option.key}>
                      {option.name ?? t(`documentType.${option.key}`, { defaultValue: option.key })}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {fieldChoices.length > 0 && (
              <div className="col-span-2 flex items-end gap-2 sm:col-span-1">
                <div className="flex flex-col gap-1">
                  <Label htmlFor="inbox-field" className="text-xs text-muted-foreground">
                    {t("inbox.filters.field")}
                  </Label>
                  <Select
                    value={search.field ?? ALL}
                    onValueChange={(value) => {
                      setSearch({ field: value === ALL ? undefined : value, value: value === ALL ? undefined : search.value });
                      if (value === ALL) setDraftValue("");
                    }}
                  >
                    <SelectTrigger id="inbox-field" className="w-full sm:w-44" aria-label={t("inbox.filters.field")}>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value={ALL}>{t("inbox.filters.anyField")}</SelectItem>
                      {fieldChoices.map((option) => (
                        <SelectItem key={option.path} value={option.path}>
                          {option.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </div>
                {filterField && (
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="inbox-field-value" className="text-xs text-muted-foreground">
                      {t("inbox.filters.fieldValue", { field: filterField.name })}
                    </Label>
                    <Input
                      id="inbox-field-value"
                      value={draftValue}
                      onChange={(event) => setDraftValue(event.target.value)}
                      placeholder={filterField.kind === "date" ? "yyyy-mm-dd" : undefined}
                      className="w-full sm:w-40"
                    />
                  </div>
                )}
              </div>
            )}
            {(layoutsOn || newLayoutFilter) && (
              <Toggle
                variant="outline"
                pressed={newLayoutFilter}
                onPressedChange={(on) => setSearch({ flag: on ? "NEW_LAYOUT" : undefined, pendingLayoutVersionId: undefined })}
                aria-label={t("inbox.filters.newLayouts")}
                data-testid="inbox-new-layouts"
              >
                {search.pendingLayoutVersionId ? t("inbox.filters.pendingLayout") : t("inbox.filters.newLayouts")}
              </Toggle>
            )}
            {(dueOn || dueSort) && (
              <Toggle
                variant="outline"
                pressed={dueSort}
                onPressedChange={(on) => setSearch({ sort: on ? "reviewDue" : undefined })}
                aria-label={t("inbox.filters.urgentFirst")}
                data-testid="inbox-sort-due"
              >
                {t("inbox.filters.urgentFirst")}
              </Toggle>
            )}
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
                      setDraftValue("");
                      setSearch({ type: undefined, supplierId: undefined, from: undefined, to: undefined, q: undefined, field: undefined, value: undefined, flag: undefined, pendingLayoutVersionId: undefined });
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

type InboxRow = components["schemas"]["DocumentSummary"];

/**
 * The total, or for a document without one that has a validity period or a row count (a price list, docs/11 §6.2) the
 * validity and the number of rows instead.
 */
function TotalOrValidity({ document }: { document: InboxRow }) {
  const { t, i18n } = useTranslation();
  const rows = document.lineCount === null || document.lineCount === undefined ? null : Number(document.lineCount);
  if (document.totalGross !== null || (!document.validFrom && !document.validUntil && rows === null)) {
    return <>{formatMoney(document.totalGross, document.currency, i18n.language)}</>;
  }
  const parts: string[] = [];
  if (document.validFrom || document.validUntil) {
    parts.push(t("inbox.validity", { from: formatDate(document.validFrom ?? null, i18n.language), until: formatDate(document.validUntil ?? null, i18n.language) }));
  }
  if (rows !== null) parts.push(t("inbox.rows", { count: rows }));
  return <span title={parts.join(" · ")}>{parts.join(" · ")}</span>;
}

/** An indexed field's value in the list, as the field index holds it (identifiers normalised, dates ISO). */
function IndexedValue({ field, value }: { field: IndexedFieldOption; value: string | undefined }) {
  const { i18n } = useTranslation();
  if (value === undefined || value === "") return <span className="text-muted-foreground">—</span>;
  if (field.kind === "date") return <>{formatDate(value, i18n.language)}</>;
  return <span className="tabular-nums">{value}</span>;
}

/** The open review's due time (docs/11 §11 `dueBy`): red when past, amber within a few hours. */
function ReviewDue({ dueAt }: { dueAt: string | null }) {
  const { t, i18n } = useTranslation();
  const state = dueState(dueAt);
  if (!dueAt || !state) return <span className="text-muted-foreground">—</span>;
  return (
    <time dateTime={dueAt} title={dueAt} data-due={state} className={state === "overdue" ? "font-medium text-destructive" : state === "soon" ? "font-medium text-amber-700 dark:text-amber-400" : undefined}>
      {state === "overdue" ? t("inbox.dueOverdue", { when: relativeTime(dueAt, i18n.language) }) : relativeTime(dueAt, i18n.language)}
    </time>
  );
}

/** "Replaced by" with a link to the list that superseded this one (`REVISION_OF` resolved as supersedes). */
function SupersededLink({ document }: { document: DocumentSummary }) {
  const { t } = useTranslation();
  if (!document.supersededBy) return null;
  return (
    <Link to="/documents/$documentId" params={{ documentId: document.supersededBy }} onClick={(event) => event.stopPropagation()} className="text-xs text-muted-foreground underline underline-offset-2" data-testid="inbox-superseded">
      {t("inbox.supersededBy")}
    </Link>
  );
}

/** The open review task's priority when it is above normal (docs/11 §6.2 `ReviewTask.Priority`). */
function PriorityBadge({ priority }: { priority: InboxRow["reviewPriority"] | undefined }) {
  const { t } = useTranslation();
  if (priority !== "High" && priority !== "Urgent") return null;
  return <Badge variant={priority === "Urgent" ? "destructive" : "warning"}>{t(`inbox.priority.${priority}`)}</Badge>;
}
