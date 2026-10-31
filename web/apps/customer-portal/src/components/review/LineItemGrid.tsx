import { Fragment, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useWatch, type FieldValues } from "react-hook-form";
import { AlertTriangle, CheckCircle2, MinusCircle } from "lucide-react";
import { Badge, Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow, cn } from "@docproc/ui";
import { formatNumber, isLowConfidence } from "@docproc/app-shell";
import { FieldInput, type HighlightHandler } from "./FieldInput";
import { useFieldLabel } from "./CanonicalForm";
import { fieldOf, visibleLineColumns, type CanonicalInvoice, type CanonicalLine, type FieldSpec, type LineColumn } from "@/lib/canonical";
import { parseAmount } from "@/lib/corrections";
import { checkLineArithmetic, type LineArithmeticResult } from "@/lib/lineArithmetic";

export interface LineItemGridProps {
  canonical: CanonicalInvoice | null;
  disabled: boolean;
  threshold?: number;
  onHighlight: HighlightHandler;
  /** Lines named by flags (`lines[2].lineNet`), highlighted in the grid. */
  flaggedLines?: ReadonlySet<number>;
  /** The canonical paths flags name: an optional column a flag points at is shown even when it is empty. */
  flaggedPaths?: readonly string[];
}

function textOf(values: unknown, index: number, name: string): string {
  const lines = (values as { lines?: Array<Record<string, unknown>> } | undefined)?.lines;
  const value = lines?.[index]?.[name];
  return typeof value === "string" ? value : "";
}

/** Width of a value column's input: numbers are short, amounts a little longer, the unit select fits "Kilogram". */
function widthOf(column: LineColumn): string {
  if (column.kind === "money") return "w-24";
  if (column.kind === "number") return "w-16";
  if (column.kind === "enum") return "w-28";
  return "w-32";
}

/**
 * The line-item grid (docs/05 "custom components"): the review form's inputs for `lines[i].*`, two table rows per line
 * so the whole line fits the form panel without scrolling sideways. The first row holds the position, the description
 * across the full width and the line's arithmetic check (quantity × unit price − discount vs. line net, within the
 * validator's tolerance, computed from what is currently typed, so a correction turns it green before it is saved);
 * the second row holds the amounts that check is about. Optional columns (SKU, discounts, line gross) appear only when
 * a line carries one or a flag names one.
 */
export function LineItemGrid({ canonical, disabled, threshold, onHighlight, flaggedLines, flaggedPaths }: LineItemGridProps) {
  const { t, i18n } = useTranslation();
  const label = useFieldLabel();
  const watched = useWatch<FieldValues>({ name: "lines" });
  const values = useMemo(() => ({ lines: watched as Array<Record<string, unknown>> | undefined }), [watched]);

  const lines = useMemo<CanonicalLine[]>(() => canonical?.lines ?? [], [canonical]);
  const columns = useMemo(() => visibleLineColumns(lines, flaggedPaths), [lines, flaggedPaths]);
  const description = columns.find((column) => column.name === "description");
  const valueColumns = columns.filter((column) => column.name !== "description");
  const currency = typeof canonical?.currency?.value === "string" ? canonical.currency.value : null;

  const arithmetic = useMemo<LineArithmeticResult[]>(
    () =>
      lines.map((_, index) =>
        checkLineArithmetic({
          quantity: parseAmount(textOf(values, index, "quantity")),
          unitPrice: parseAmount(textOf(values, index, "unitPrice")),
          lineNet: parseAmount(textOf(values, index, "lineNet")),
          discountPercent: parseAmount(textOf(values, index, "discountPercent")),
          discountAmount: parseAmount(textOf(values, index, "discountAmount")),
        }),
      ),
    [lines, values],
  );

  if (lines.length === 0) {
    return <p className="text-sm text-muted-foreground">{t("review.lines.empty")}</p>;
  }

  const mismatches = arithmetic.filter((a) => a.status === "mismatch").length;

  const cell = (index: number, line: CanonicalLine, column: LineColumn) => {
    const path = `lines[${index}].${column.name}`;
    const spec: FieldSpec = { path, kind: column.kind, fieldPath: path, options: column.options };
    const field = fieldOf(canonical, spec);
    // The unit as the invoice prints it ("Stk", "kg"), beside the normalised unit the select edits.
    const printed = column.name === "unit" ? line.unitRaw?.trim() : undefined;
    const input = (
      <FieldInput
        spec={spec}
        field={field}
        compact
        disabled={disabled}
        threshold={threshold}
        onHighlight={onHighlight}
        aria-label={t("review.lines.cellLabel", { line: line.lineNumber, field: label(spec) })}
        className={column.name === "description" ? "w-full" : widthOf(column)}
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
  };

  const headerOf = (column: LineColumn) => {
    const name = label(`lines[0].${column.name}`);
    // The amounts are in the document's currency; the cells leave it out so the line fits the panel.
    return column.kind === "money" && currency ? t("review.lines.inCurrency", { field: name, currency }) : name;
  };

  return (
    <div className="flex flex-col gap-2" data-testid="line-item-grid">
      <div className="flex items-center gap-2 text-sm">
        <span className="font-semibold">{t("review.lines.title", { count: lines.length })}</span>
        {mismatches > 0 ? (
          <Badge variant="destructive">
            <AlertTriangle aria-hidden />
            {t("review.lines.mismatches", { count: mismatches })}
          </Badge>
        ) : (
          <Badge variant="success">
            <CheckCircle2 aria-hidden />
            {t("review.lines.allOk")}
          </Badge>
        )}
      </div>
      <div className="overflow-x-auto rounded-lg border">
        <Table className="[&_td]:px-1.5 [&_th]:px-1.5">
          <TableCaption className="sr-only">{t("review.lines.caption")}</TableCaption>
          <TableHeader>
            {/*
              "#" and "Check" span both header rows and sit at the bottom; pb-1.5 puts their text on the line of the
              second row's labels, which are centred in h-8 (32 px - 20 px line) / 2 = 6 px.
            */}
            <TableRow>
              <TableHead rowSpan={2} className="w-8 pb-1.5 align-bottom">
                {t("fields.line.lineNumber")}
              </TableHead>
              <TableHead colSpan={valueColumns.length}>{description ? headerOf(description) : null}</TableHead>
              <TableHead rowSpan={2} className="w-12 pb-1.5 align-bottom whitespace-nowrap">
                {t("review.lines.check")}
              </TableHead>
            </TableRow>
            <TableRow>
              {valueColumns.map((column) => (
                <TableHead key={column.name} className={cn("h-8 whitespace-nowrap", (column.kind === "money" || column.kind === "number") && "text-right")}>
                  {headerOf(column)}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          {lines.map((line, index) => {
            const result = arithmetic[index];
            const low = columns.some((column) => isLowConfidence(line[column.name as keyof CanonicalLine] as never, threshold));
            const rowClass = cn(
              result?.status === "mismatch" && "bg-destructive/5",
              flaggedLines?.has(index) && "bg-amber-500/5",
              // Amber like the confidence badge: a low confidence is a warning to check, not a failure.
      low && "[&>td:first-child]:border-l-2 [&>td:first-child]:border-l-amber-500",
            );
            return (
              <TableBody key={index} data-line={index} data-arithmetic={result?.status} className="border-b last:border-b-0 [&_tr]:border-b-0">
                <TableRow data-arithmetic={result?.status} className={cn(rowClass, "hover:bg-transparent")}>
                  <TableCell rowSpan={2} className="align-top tabular-nums text-muted-foreground">
                    {line.lineNumber}
                  </TableCell>
                  <TableCell colSpan={valueColumns.length} className="pb-1">
                    {description ? cell(index, line, description) : null}
                  </TableCell>
                  <TableCell rowSpan={2} className="align-top">
                    <ArithmeticIndicator result={result} locale={i18n.language} />
                  </TableCell>
                </TableRow>
                <TableRow className={cn(rowClass, "hover:bg-transparent")}>
                  {valueColumns.map((column) => (
                    <Fragment key={column.name}>
                      <TableCell className="pt-0 align-top">{cell(index, line, column)}</TableCell>
                    </Fragment>
                  ))}
                </TableRow>
              </TableBody>
            );
          })}
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
      <Badge variant="success" aria-label={t("review.lines.ok")} title={t("review.lines.okDetail", { expected: formatNumber(result.expected, locale) })} data-arithmetic="ok">
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
