import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useWatch, type FieldValues } from "react-hook-form";
import { flexRender, useTable } from "@tanstack/react-table";
import { AlertTriangle, CheckCircle2, MinusCircle } from "lucide-react";
import { Badge, Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow, cn } from "@docproc/ui";
import { createDataTableColumnHelper, dataTableFeatures, formatNumber, isLowConfidence } from "@docproc/app-shell";
import { FieldInput, type HighlightHandler } from "./FieldInput";
import { useFieldLabel } from "./CanonicalForm";
import { currencyOf, fieldOf, lineColumns, type CanonicalInvoice, type CanonicalLine, type FieldSpec } from "@/lib/canonical";
import { parseAmount } from "@/lib/corrections";
import { checkLineArithmetic, type LineArithmeticResult } from "@/lib/lineArithmetic";

export interface LineItemGridProps {
  canonical: CanonicalInvoice | null;
  disabled: boolean;
  threshold?: number;
  onHighlight: HighlightHandler;
  /** Lines named by flags (`lines[2].lineNet`), highlighted in the grid. */
  flaggedLines?: ReadonlySet<number>;
}

interface GridRow {
  index: number;
  line: CanonicalLine;
}

const EMPTY_ROWS: GridRow[] = [];
const helper = createDataTableColumnHelper<GridRow>();

function textOf(values: unknown, index: number, name: string): string {
  const lines = (values as { lines?: Array<Record<string, unknown>> } | undefined)?.lines;
  const value = lines?.[index]?.[name];
  return typeof value === "string" ? value : "";
}

/**
 * The line-item grid (docs/05 "custom components"): a TanStack Table whose cells are the review form's inputs for
 * `lines[i].*`, with a per-line arithmetic indicator (quantity × unit price − discount vs. line net, within the
 * validator's tolerance)
 * computed from what is currently typed, so a correction turns the indicator green before it is saved.
 */
export function LineItemGrid({ canonical, disabled, threshold, onHighlight, flaggedLines }: LineItemGridProps) {
  const { t, i18n } = useTranslation();
  const label = useFieldLabel();
  const watched = useWatch<FieldValues>({ name: "lines" });
  const values = useMemo(() => ({ lines: watched as Array<Record<string, unknown>> | undefined }), [watched]);

  const rows = useMemo<GridRow[]>(() => (canonical?.lines ?? []).map((line, index) => ({ index, line })), [canonical]);

  const arithmetic = useMemo<LineArithmeticResult[]>(
    () =>
      rows.map(({ index }) =>
        checkLineArithmetic({
          quantity: parseAmount(textOf(values, index, "quantity")),
          unitPrice: parseAmount(textOf(values, index, "unitPrice")),
          lineNet: parseAmount(textOf(values, index, "lineNet")),
          discountPercent: parseAmount(textOf(values, index, "discountPercent")),
          discountAmount: parseAmount(textOf(values, index, "discountAmount")),
        }),
      ),
    [rows, values],
  );

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor((row) => row.line.lineNumber, {
          id: "lineNumber",
          header: t("fields.line.lineNumber"),
          cell: (info) => <span className="tabular-nums text-muted-foreground">{info.getValue()}</span>,
        }),
        // Right after the line number: the grid scrolls horizontally, and the check is what a reviewer looks for first.
        helper.display({
          id: "arithmetic",
          header: t("review.lines.check"),
          cell: ({ row }) => <ArithmeticIndicator result={arithmetic[row.original.index]} locale={i18n.language} />,
        }),
        ...lineColumns.map((column) =>
          helper.display({
            id: column.name,
            header: label(`lines[0].${column.name}`),
            cell: ({ row }) => {
              const spec: FieldSpec = { path: `lines[${row.original.index}].${column.name}`, kind: column.kind, fieldPath: `lines[${row.original.index}].${column.name}`, options: column.options };
              const field = fieldOf(canonical, spec);
              // The unit as the invoice prints it ("Stk", "kg"), beside the normalised unit the select edits.
              const printed = column.name === "unit" ? row.original.line.unitRaw?.trim() : undefined;
              const input = (
                <FieldInput
                  spec={spec}
                  field={field}
                  compact
                  disabled={disabled}
                  threshold={threshold}
                  onHighlight={onHighlight}
                  currency={column.kind === "money" ? currencyOf(canonical, spec) : undefined}
                  aria-label={t("review.lines.cellLabel", { line: row.original.line.lineNumber, field: label(spec) })}
                  className={cn(column.name === "description" ? "min-w-48" : column.kind === "money" || column.kind === "number" ? "w-24" : "w-28")}
                />
              );
              if (!printed) return input;
              return (
                <div className="flex flex-col gap-0.5">
                  {input}
                  <span className="text-xs text-muted-foreground" data-testid="unit-raw">
                    {t("review.lines.unitRaw", { unit: printed })}
                  </span>
                </div>
              );
            },
          }),
        ),
      ]),
    [t, i18n.language, label, canonical, disabled, threshold, onHighlight, arithmetic],
  );

  const table = useTable({ features: dataTableFeatures, columns, data: rows.length ? rows : EMPTY_ROWS, getRowId: (row) => String(row.index) });

  if (rows.length === 0) {
    return <p className="text-sm text-muted-foreground">{t("review.lines.empty")}</p>;
  }

  const mismatches = arithmetic.filter((a) => a.status === "mismatch").length;

  return (
    <div className="flex flex-col gap-2" data-testid="line-item-grid">
      <div className="flex items-center gap-2 text-sm">
        <span className="font-semibold">{t("review.lines.title", { count: rows.length })}</span>
        {mismatches > 0 ? (
          <Badge variant="destructive">
            <AlertTriangle aria-hidden />
            {t("review.lines.mismatches", { count: mismatches })}
          </Badge>
        ) : (
          <Badge variant="secondary">
            <CheckCircle2 aria-hidden />
            {t("review.lines.allOk")}
          </Badge>
        )}
      </div>
      <div className="overflow-x-auto rounded-lg border">
        <Table>
          <TableCaption className="sr-only">{t("review.lines.caption")}</TableCaption>
          <TableHeader>
            {table.getHeaderGroups().map((headerGroup) => (
              <TableRow key={headerGroup.id}>
                {headerGroup.headers.map((header) => (
                  <TableHead key={header.id} className="whitespace-nowrap">
                    {header.isPlaceholder ? null : flexRender(header.column.columnDef.header, header.getContext())}
                  </TableHead>
                ))}
              </TableRow>
            ))}
          </TableHeader>
          <TableBody>
            {table.getRowModel().rows.map((row) => {
              const result = arithmetic[row.original.index];
              const low = lineColumns.some((column) => isLowConfidence(row.original.line[column.name as keyof CanonicalLine] as never, threshold));
              return (
                <TableRow
                  key={row.id}
                  data-line={row.original.index}
                  data-arithmetic={result?.status}
                  className={cn(result?.status === "mismatch" && "bg-destructive/5", flaggedLines?.has(row.original.index) && "outline outline-1 outline-destructive/40", low && "[&>td:first-child]:border-l-2 [&>td:first-child]:border-l-destructive")}
                >
                  {row.getVisibleCells().map((cell) => (
                    <TableCell key={cell.id} className="align-top">
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </TableCell>
                  ))}
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}

export function ArithmeticIndicator({ result, locale }: { result: LineArithmeticResult | undefined; locale?: string }) {
  const { t } = useTranslation();
  if (!result || result.status === "incomplete") {
    return (
      <Badge variant="outline" aria-label={t("review.lines.incomplete")} title={t("review.lines.incomplete")} data-arithmetic="incomplete">
        <MinusCircle aria-hidden />
        <span className="sr-only">{t("review.lines.incomplete")}</span>
      </Badge>
    );
  }
  if (result.status === "ok") {
    return (
      <Badge variant="secondary" aria-label={t("review.lines.ok")} title={t("review.lines.okDetail", { expected: formatNumber(result.expected, locale) })} data-arithmetic="ok">
        <CheckCircle2 aria-hidden />
        <span className="sr-only">{t("review.lines.ok")}</span>
      </Badge>
    );
  }
  const detail = t("review.lines.mismatchDetail", { expected: formatNumber(result.expected, locale), actual: formatNumber(result.actual, locale) });
  return (
    <Badge variant="destructive" aria-label={detail} title={detail} data-arithmetic="mismatch">
      <AlertTriangle aria-hidden />
      <span className="tabular-nums">{formatNumber(result.difference, locale, { signDisplay: "always", minimumFractionDigits: 2, maximumFractionDigits: 2 })}</span>
    </Badge>
  );
}
