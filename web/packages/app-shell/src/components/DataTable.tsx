import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
  columnVisibilityFeature,
  createColumnHelper,
  createSortedRowModel,
  flexRender,
  rowSortingFeature,
  sortFn_alphanumeric,
  sortFn_basic,
  sortFn_datetime,
  sortFn_text,
  tableFeatures,
  useTable,
  type OnChangeFn,
  type RowData,
  type SortingState,
  type TableOptions,
} from "@tanstack/react-table";
import { ArrowDown, ArrowUp, ArrowUpDown, Settings2 } from "lucide-react";
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

/**
 * The feature set every grid of the apps shares (TanStack Table v9 registers features explicitly): client-side
 * sorting with the stock comparators and column visibility. Pagination is the API's cursor, not the table's.
 */
export const dataTableFeatures = tableFeatures({
  rowSortingFeature,
  columnVisibilityFeature,
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
  /** Screen-reader caption; shown visually only with `showCaption`. */
  caption?: string;
  showCaption?: boolean;
  className?: string;
  skeletonRows?: number;
}

const EMPTY_SORTING: SortingState = [];

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
  caption,
  showCaption = false,
  className,
  skeletonRows = 5,
}: DataTableProps<TData>) {
  const { t } = useTranslation("common");
  const [uncontrolledSorting, setUncontrolledSorting] = useState<SortingState>(initialSorting);
  const controlled = sorting !== undefined && onSortingChange !== undefined;

  const table = useTable({
    features: dataTableFeatures,
    columns,
    data,
    getRowId,
    state: { sorting: controlled ? sorting : uncontrolledSorting },
    onSortingChange: controlled ? onSortingChange : setUncontrolledSorting,
    initialState: initialColumnVisibility ? { columnVisibility: initialColumnVisibility } : undefined,
    enableSortingRemoval: true,
    enableMultiSort: false,
  });

  const headerGroups = table.getHeaderGroups();
  const rows = table.getRowModel().rows;
  const visibleColumnCount = table.getVisibleLeafColumns().length || 1;
  const showEmpty = !isLoading && rows.length === 0;

  return (
    <div className={cn("flex flex-col gap-3", className)}>
      {(toolbar || enableColumnVisibility) && (
        <div className="flex flex-wrap items-center gap-2">
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
                          aria-label={
                            sorted === "asc"
                              ? t("table.sortDescending")
                              : sorted === "desc"
                                ? t("table.clearSort")
                                : t("table.sortAscending")
                          }
                        >
                          {label}
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
            {showEmpty && (
              <TableRow>
                <TableCell colSpan={visibleColumnCount} className="p-0">
                  <EmptyState title={emptyTitle ?? t("table.empty")} description={emptyDescription ?? t("table.emptyDescription")} action={emptyAction} className="border-0" />
                </TableCell>
              </TableRow>
            )}
            {!isLoading &&
              rows.map((row) => (
                <TableRow
                  key={row.id}
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
                    <TableCell key={cell.id}>{flexRender(cell.column.columnDef.cell, cell.getContext())}</TableCell>
                  ))}
                </TableRow>
              ))}
          </TableBody>
        </Table>
      </div>
      {footer && <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">{footer}</div>}
    </div>
  );
}
