import { Fragment, useId, useMemo, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
  columnVisibilityFeature,
  createColumnHelper,
  createSortedRowModel,
  flexRender,
  rowExpandingFeature,
  rowSortingFeature,
  sortFn_alphanumeric,
  sortFn_basic,
  sortFn_datetime,
  sortFn_text,
  tableFeatures,
  useTable,
  type ExpandedState,
  type OnChangeFn,
  type RowData,
  type SortingState,
  type TableOptions,
} from "@tanstack/react-table";
import { AlertTriangle, ArrowDown, ArrowUp, ArrowUpDown, ChevronDown, ChevronRight, RotateCcw, Settings2 } from "lucide-react";
import {
  Button,
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  Skeleton,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  cn,
} from "@docproc/ui";
import { EmptyState } from "./EmptyState";
import { ApiError, errorMessage } from "../api/ApiError";

/** What `DataTable` needs of the query behind its rows: a `useQuery` result and a `useCursorList` result both fit. */
export interface DataTableQueryState {
  isError: boolean;
  error: unknown;
  refetch: () => unknown;
}

/** What a failed list request reports, for the table's error state: the problem detail, else its message. */
export function loadErrorDescription(error: unknown): string | undefined {
  if (error instanceof ApiError) return error.problem?.detail ?? error.message;
  return error ? errorMessage(error) : undefined;
}

/**
 * The feature set every grid of the apps shares (TanStack Table v9 registers features explicitly): client-side
 * sorting with the stock comparators, column visibility and row expanding (a detail panel under a row, see
 * `renderSubRow`). Pagination is the API's cursor, not the table's.
 */
export const dataTableFeatures = tableFeatures({
  rowSortingFeature,
  columnVisibilityFeature,
  rowExpandingFeature,
  sortedRowModel: createSortedRowModel(),
  sortFns: { alphanumeric: sortFn_alphanumeric, text: sortFn_text, datetime: sortFn_datetime, basic: sortFn_basic },
});

export type DataTableFeatures = typeof dataTableFeatures;

/** `const columns = createDataTableColumnHelper<DocumentSummary>()` → `helper.columns([helper.accessor("status", {...})])`. */
export function createDataTableColumnHelper<TData extends RowData>() {
  return createColumnHelper<DataTableFeatures, TData>();
}

export type DataTableColumns<TData extends RowData> = TableOptions<DataTableFeatures, TData>["columns"];

export interface DataTableProps<TData extends RowData> {
  /** Keep this stable (module scope or `useMemo`); a new array every render rebuilds the row model. */
  columns: DataTableColumns<TData>;
  /** Likewise stable: pass `query.data?.items ?? EMPTY_ARRAY`, not a fresh `[]`. */
  data: TData[];
  getRowId?: (row: TData, index: number) => string;
  isLoading?: boolean;
  /** Uncontrolled sorting starts here. */
  initialSorting?: SortingState;
  /** Controlled sorting: pass both or neither. */
  sorting?: SortingState;
  onSortingChange?: OnChangeFn<SortingState>;
  /** Columns hidden at first (`{ internalId: false }`); the user can show them from the column menu. */
  initialColumnVisibility?: Record<string, boolean>;
  /** Shows the column-visibility menu in the toolbar. */
  enableColumnVisibility?: boolean;
  /** Toolbar content to the left of the column menu (filters, search). */
  toolbar?: ReactNode;
  /** Rendered under the rows (a "Load more" button for cursor pagination, a row count). */
  footer?: ReactNode;
  onRowClick?: (row: TData) => void;
  rowClassName?: (row: TData) => string | undefined;
  emptyTitle?: ReactNode;
  emptyDescription?: ReactNode;
  emptyAction?: ReactNode;
  /**
   * The query the rows come from. When it failed, the table says so (with Retry) instead of showing its empty state:
   * "nothing here" must never stand in for "the list did not load".
   */
  query?: DataTableQueryState;
  /**
   * Makes rows expandable: a leading chevron column toggles a full-width panel under the row with this content.
   * Expanded rows are kept by row id across refetches and re-sorts (pass `getRowId`).
   */
  renderSubRow?: (row: TData) => ReactNode;
  /** Which rows get a chevron when `renderSubRow` is set (default: every row). */
  getRowCanExpand?: (row: TData) => boolean;
  /** Extra attributes for a row's `<tr>` (`data-*` hooks for tests and styling). */
  rowAttributes?: (row: TData) => Record<`data-${string}`, string | undefined>;
  /** Screen-reader caption; shown visually only with `showCaption`. */
  caption?: string;
  showCaption?: boolean;
  className?: string;
  skeletonRows?: number;
}

const EMPTY_SORTING: SortingState = [];
const EXPANDER_ID = "__expand";

export function DataTable<TData extends RowData>({
  columns,
  data,
  getRowId,
  isLoading = false,
  initialSorting = EMPTY_SORTING,
  sorting,
  onSortingChange,
  initialColumnVisibility,
  enableColumnVisibility = false,
  toolbar,
  footer,
  onRowClick,
  rowClassName,
  emptyTitle,
  emptyDescription,
  emptyAction,
  query,
  renderSubRow,
  getRowCanExpand,
  rowAttributes,
  caption,
  showCaption = false,
  className,
  skeletonRows = 5,
}: DataTableProps<TData>) {
  const { t } = useTranslation("common");
  const [uncontrolledSorting, setUncontrolledSorting] = useState<SortingState>(initialSorting);
  const [expanded, setExpanded] = useState<ExpandedState>({});
  const controlled = sorting !== undefined && onSortingChange !== undefined;
  const expandable = renderSubRow !== undefined;

  // The chevron column is the table's own, so screens only describe their data columns.
  const allColumns = useMemo<DataTableColumns<TData>>(() => {
    if (!expandable) return columns;
    const helper = createColumnHelper<DataTableFeatures, TData>();
    return [
      helper.display({
        id: EXPANDER_ID,
        enableHiding: false,
        enableSorting: false,
        header: () => <span className="sr-only">{t("table.details")}</span>,
        cell: ({ row }) =>
          row.getCanExpand() ? (
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              aria-expanded={row.getIsExpanded()}
              aria-controls={`row-details-${row.id}`}
              aria-label={row.getIsExpanded() ? t("table.collapseRow") : t("table.expandRow")}
              onClick={(event) => {
                event.stopPropagation();
                row.toggleExpanded();
              }}
            >
              {row.getIsExpanded() ? <ChevronDown aria-hidden /> : <ChevronRight aria-hidden />}
            </Button>
          ) : null,
      }),
      ...columns,
    ] as DataTableColumns<TData>;
  }, [columns, expandable, t]);

  const table = useTable({
    features: dataTableFeatures,
    columns: allColumns,
    data,
    getRowId,
    state: { sorting: controlled ? sorting : uncontrolledSorting, expanded },
    onSortingChange: controlled ? onSortingChange : setUncontrolledSorting,
    onExpandedChange: setExpanded,
    getRowCanExpand: expandable ? (row) => getRowCanExpand?.(row.original) ?? true : () => false,
    // A refetch (polling) hands a new array; keep what the user opened.
    autoResetExpanded: false,
    initialState: initialColumnVisibility ? { columnVisibility: initialColumnVisibility } : undefined,
    enableSortingRemoval: true,
    enableMultiSort: false,
  });

  const headerGroups = table.getHeaderGroups();
  const rows = table.getRowModel().rows;
  const visibleColumnCount = table.getVisibleLeafColumns().length || 1;
  const failed = query?.isError === true;
  const showError = failed && !isLoading && rows.length === 0;
  const showEmpty = !failed && !isLoading && rows.length === 0;
  const sortHintId = useId();
  const retry = query ? (
    <Button type="button" variant="outline" size="sm" onClick={() => void query.refetch()}>
      <RotateCcw aria-hidden />
      {t("errors.tryAgain")}
    </Button>
  ) : null;

  return (
    <div className={cn("flex flex-col gap-3", className)}>
      {(toolbar || enableColumnVisibility) && (
        // items-start: when the filters wrap onto a second row, Columns stays level with the first one instead of
        // floating halfway between the two.
        <div className="flex flex-wrap items-start gap-2">
          <div className="flex flex-1 flex-wrap items-center gap-2">{toolbar}</div>
          {enableColumnVisibility && (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="outline" size="sm" aria-label={t("table.toggleColumns")}>
                  <Settings2 aria-hidden />
                  <span className="hidden sm:inline">{t("table.columns")}</span>
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="min-w-44">
                <DropdownMenuLabel>{t("table.columns")}</DropdownMenuLabel>
                <DropdownMenuSeparator />
                {table
                  .getAllLeafColumns()
                  .filter((column) => column.getCanHide())
                  .map((column) => (
                    <DropdownMenuCheckboxItem
                      key={column.id}
                      checked={column.getIsVisible()}
                      onCheckedChange={(checked) => column.toggleVisibility(checked === true)}
                      onSelect={(event) => event.preventDefault()}
                    >
                      {typeof column.columnDef.header === "string" ? column.columnDef.header : column.id}
                    </DropdownMenuCheckboxItem>
                  ))}
              </DropdownMenuContent>
            </DropdownMenu>
          )}
        </div>
      )}

      {failed && rows.length > 0 && (
        <div role="alert" className="flex flex-wrap items-center gap-2 rounded-lg border border-destructive/40 px-3 py-2 text-sm" data-testid="table-refresh-error">
          <AlertTriangle className="size-4 text-destructive" aria-hidden />
          <span className="flex-1">{t("table.refreshFailed")}</span>
          {retry}
        </div>
      )}
      <div className="overflow-hidden rounded-lg border">
        <Table>
          {caption && <TableCaption className={showCaption ? undefined : "sr-only"}>{caption}</TableCaption>}
          <TableHeader>
            {headerGroups.map((headerGroup) => (
              <TableRow key={headerGroup.id}>
                {headerGroup.headers.map((header) => {
                  const canSort = header.column.getCanSort();
                  const sorted = header.column.getIsSorted();
                  const ariaSort = sorted === "asc" ? "ascending" : sorted === "desc" ? "descending" : canSort ? "none" : undefined;
                  const label = flexRender(header.column.columnDef.header, header.getContext());
                  const nextOrder = canSort ? header.column.getNextSortingOrder() : false;
                  const sortHint = nextOrder === "asc" ? t("table.sortAscending") : nextOrder === "desc" ? t("table.sortDescending") : t("table.clearSort");
                  return (
                    <TableHead key={header.id} colSpan={header.colSpan} aria-sort={ariaSort}>
                      {header.isPlaceholder ? null : canSort ? (
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="-ml-3 h-8 data-[sorted=true]:text-foreground"
                          data-sorted={sorted !== false}
                          onClick={header.column.getToggleSortingHandler()}
                          // The column's text stays the name of the button and the header cell; what the click does is
                          // the description (numeric and date columns sort descending first); the state is aria-sort.
                          aria-describedby={`${sortHintId}-${header.id}`}
                        >
                          {label}
                          <span id={`${sortHintId}-${header.id}`} hidden>
                            {sortHint}
                          </span>
                          {sorted === "asc" ? <ArrowUp aria-hidden /> : sorted === "desc" ? <ArrowDown aria-hidden /> : <ArrowUpDown aria-hidden className="opacity-50" />}
                        </Button>
                      ) : (
                        label
                      )}
                    </TableHead>
                  );
                })}
              </TableRow>
            ))}
          </TableHeader>
          <TableBody>
            {isLoading &&
              Array.from({ length: skeletonRows }, (_, i) => (
                <TableRow key={`skeleton-${i}`} aria-busy>
                  {Array.from({ length: visibleColumnCount }, (_, j) => (
                    <TableCell key={j}>
                      <Skeleton className="h-4 w-[60%]" />
                    </TableCell>
                  ))}
                </TableRow>
              ))}
            {showError && (
              <TableRow>
                <TableCell colSpan={visibleColumnCount} className="p-0">
                  <EmptyState icon={AlertTriangle} title={t("table.loadFailed")} description={loadErrorDescription(query?.error)} action={retry} className="border-0" />
                </TableCell>
              </TableRow>
            )}
            {showEmpty && (
              <TableRow>
                <TableCell colSpan={visibleColumnCount} className="p-0">
                  <EmptyState title={emptyTitle ?? t("table.empty")} description={emptyDescription ?? t("table.emptyDescription")} action={emptyAction} className="border-0" />
                </TableCell>
              </TableRow>
            )}
            {!isLoading &&
              rows.map((row) => (
                <Fragment key={row.id}>
                  <TableRow
                    {...rowAttributes?.(row.original)}
                    data-expanded={expandable ? row.getIsExpanded() : undefined}
                    className={cn(onRowClick && "cursor-pointer", rowClassName?.(row.original))}
                    onClick={onRowClick ? () => onRowClick(row.original) : undefined}
                    onKeyDown={
                      onRowClick
                        ? (event) => {
                            if (event.target === event.currentTarget && (event.key === "Enter" || event.key === " ")) {
                              event.preventDefault();
                              onRowClick(row.original);
                            }
                          }
                        : undefined
                    }
                    tabIndex={onRowClick ? 0 : undefined}
                  >
                    {row.getVisibleCells().map((cell) => (
                      <TableCell key={cell.id} className={cell.column.id === EXPANDER_ID ? "w-8 py-1" : undefined}>
                        {flexRender(cell.column.columnDef.cell, cell.getContext())}
                      </TableCell>
                    ))}
                  </TableRow>
                  {expandable && row.getIsExpanded() && (
                    <TableRow id={`row-details-${row.id}`} data-slot="sub-row" className="bg-muted/30 hover:bg-muted/30">
                      <TableCell colSpan={visibleColumnCount} className="whitespace-normal p-4">
                        {renderSubRow(row.original)}
                      </TableCell>
                    </TableRow>
                  )}
                </Fragment>
              ))}
          </TableBody>
        </Table>
      </div>
      {footer && <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">{footer}</div>}
    </div>
  );
}
