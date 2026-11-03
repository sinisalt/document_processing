import { Fragment, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useWatch, type FieldValues } from "react-hook-form";
import { AlertTriangle, CheckCircle2, ChevronLeft, ChevronRight, Columns3, MinusCircle, Plus, Trash2 } from "lucide-react";
import {
  Badge,
  Button,
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  cn,
} from "@docproc/ui";
import { formatNumber, getByPath, isFieldValue, isLowConfidence, type FieldValue } from "@docproc/app-shell";
import { FieldInput, type HighlightHandler } from "./FieldInput";
import type { FieldSpec, FormLayout, GridColumn, GridNode } from "../lib/formSpec";
import { REVIEW_NS, groupLabel, useFieldLabel } from "../lib/labels";
import { appendRow, parseAmount, removeAt, type FieldCorrection } from "../lib/corrections";
import { checkLineArithmetic, type LineArithmeticResult } from "../lib/lineArithmetic";
import { currencyAt, fieldOf, formName, toEditable } from "../lib/values";

/** Above this many rows the grid shows a page of rows at a time, so a 10,000-row price list does not mount 10,000 rows of inputs. */
export const PAGED_ABOVE = 200;
export const PAGE_SIZE = 100;

export interface RepeatingGroupGridProps {
  node: GridNode;
  layout: Pick<FormLayout, "fields">;
  canonical: unknown;
  disabled: boolean;
  threshold?: number;
  onHighlight: HighlightHandler;
  /** The canonical paths flags name: their rows are highlighted, and an optional column a flag points at is shown. */
  flaggedPaths?: readonly string[];
  /**
   * Adds or removes a row (`append`/`remove`, definition-driven documents only). Without it the grid edits values only,
   * as the coded patcher of a version 1 tree allows.
   */
  onStructuralChange?: (correction: FieldCorrection) => void;
  /** A structural change is on its way: the buttons wait for it. */
  busy?: boolean;
}

function textOf(rows: unknown, index: number, name: string): string {
  const value = (rows as Array<Record<string, unknown>> | undefined)?.[index]?.[name];
  return typeof value === "string" ? value : "";
}

/** Width of a value column's input: numbers are short, amounts a little longer, a code select fits "Kilogram". */
function widthOf(column: GridColumn): string {
  if (column.kind === "money") return "w-24";
  if (column.kind === "number") return "w-16";
  if (column.kind === "enum" || column.kind === "boolean") return "w-28";
  if (column.kind === "date") return "w-32";
  return "w-32";
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Any repeating group of a definition (docs/11 §12.2 `RepeatingGroupGrid`, which replaces the invoice-only line grid):
 * two table rows per row of the group so a line fits the form panel without scrolling sideways. The first holds the
 * position, the first multi-line column (the description) across the full width, and for the invoice family's lines
 * the arithmetic check (quantity × unit price − discount vs. line net, within the validator's tolerance, computed from
 * what is typed); the second holds the other columns. A column that is not always shown appears when a row has a
 * value in it or a flag names it, or when the reviewer picks it under Columns. Rows are added and removed on
 * definition-driven documents, and above {@link PAGED_ABOVE} rows the grid pages.
 */
export function RepeatingGroupGrid({ node, layout, canonical, disabled, threshold, onHighlight, flaggedPaths = [], onStructuralChange, busy = false }: RepeatingGroupGridProps) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  const label = useFieldLabel(layout);
  const isLines = node.field.key === "lines";
  const group = groupLabel(t, i18n.language, node.field);
  const watched = useWatch<FieldValues>({ name: formName(node.path) });
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const [page, setPage] = useState(0);

  const rows = useMemo(() => {
    const stored = getByPath(canonical, node.path);
    return Array.isArray(stored) ? (stored as Array<Record<string, unknown>>) : [];
  }, [canonical, node.path]);

  const flagged = useMemo(() => {
    const pattern = new RegExp(`^${escapeRegExp(node.path)}\\[(\\d+)\\](?:\\.(\\w+))?`);
    const lines = new Set<number>();
    const columns = new Set<string>();
    for (const path of flaggedPaths) {
      const match = pattern.exec(path);
      if (!match) continue;
      lines.add(Number(match[1]));
      if (match[2]) columns.add(match[2]);
    }
    return { lines, columns };
  }, [flaggedPaths, node.path]);

  const hasValue = useMemo(() => {
    const keys = new Set<string>();
    for (const column of node.columns) {
      const some = rows.some((row) => {
        const field = row?.[column.key];
        // A Missing field carries the type's default (0 for a discount): that is no value.
        const value = isFieldValue(field) && field.status !== "Missing" ? field.value : undefined;
        return toEditable(column.kind, value) !== "";
      });
      if (some) keys.add(column.key);
    }
    return keys;
  }, [node.columns, rows]);

  const shown = (column: GridColumn) => column.alwaysShown || hasValue.has(column.key) || flagged.columns.has(column.key) || picked.has(column.key);
  const columns = node.columns.filter(shown);
  const optional = node.columns.filter((column) => !column.alwaysShown && !hasValue.has(column.key) && !flagged.columns.has(column.key));
  const wide = columns.find((column) => column.wide);
  const valueColumns = columns.filter((column) => column !== wide);
  const nestedCount = node.nested.length;
  const span = Math.max(1, valueColumns.length + nestedCount);

  const arithmetic = useMemo<LineArithmeticResult[]>(
    () =>
      node.arithmetic
        ? rows.map((_, index) =>
            checkLineArithmetic({
              quantity: parseAmount(textOf(watched, index, "quantity")),
              unitPrice: parseAmount(textOf(watched, index, "unitPrice")),
              lineNet: parseAmount(textOf(watched, index, "lineNet")),
              discountPercent: parseAmount(textOf(watched, index, "discountPercent")),
              discountAmount: parseAmount(textOf(watched, index, "discountAmount")),
            }),
          )
        : [],
    [node.arithmetic, rows, watched],
  );

  const paged = rows.length > PAGED_ABOVE;
  const pageCount = paged ? Math.ceil(rows.length / PAGE_SIZE) : 1;
  const current = Math.min(page, pageCount - 1);
  const first = paged ? current * PAGE_SIZE : 0;
  const last = paged ? Math.min(rows.length, first + PAGE_SIZE) : rows.length;

  const canEdit = !disabled && !!onStructuralChange;
  const max = node.field.maxItems;
  const full = max !== undefined && rows.length >= max;

  const addButton = canEdit ? (
    <Button type="button" variant="outline" size="sm" onClick={() => onStructuralChange?.(appendRow(node.path))} disabled={busy || full} title={full ? t("grid.full", { max }) : undefined} data-testid={`add-row-${node.path}`}>
      <Plus aria-hidden />
      {t("grid.addRow")}
    </Button>
  ) : null;

  if (rows.length === 0) {
    return (
      <div className="flex flex-col items-start gap-2" data-testid="repeating-group" data-group={node.path}>
        {!isLines && <span className="text-sm font-semibold">{group}</span>}
        <p className="text-sm text-muted-foreground">{isLines ? t("lines.empty") : t("grid.empty")}</p>
        {addButton}
      </div>
    );
  }

  const mismatches = arithmetic.filter((a) => a.status === "mismatch").length;
  const lineNumberOf = (row: Record<string, unknown>, index: number) => {
    const number = node.rowNumberKey ? row?.[node.rowNumberKey] : undefined;
    return typeof number === "number" ? number : index + 1;
  };

  const cell = (index: number, row: Record<string, unknown>, column: GridColumn) => {
    const path = `${node.path}[${index}].${column.key}`;
    const spec: FieldSpec = {
      path,
      kind: column.kind,
      fieldPath: path,
      ...(column.options ? { options: column.options } : {}),
      ...(column.optionLabels ? { optionLabels: column.optionLabels } : {}),
      definitionKind: column.field.kind,
      ...(column.field.readOnly ? { readOnly: true } : {}),
      ...(column.field.currencyPath ? { currencyPath: column.field.currencyPath } : {}),
      ...(column.integer ? { integer: true } : {}),
    };
    const field = fieldOf(canonical, spec);
    const fieldLabel = label(spec);
    const input = (
      <FieldInput
        spec={spec}
        field={field}
        compact
        disabled={disabled}
        threshold={threshold}
        onHighlight={onHighlight}
        aria-label={isLines ? t("lines.cellLabel", { line: lineNumberOf(row, index), field: fieldLabel }) : t("grid.cellLabel", { group, row: index + 1, field: fieldLabel })}
        className={column === wide ? "w-full" : widthOf(column)}
      />
    );
    // The unit as the document prints it ("Stk", "kg"), beside the normalised unit the select edits.
    const printed = column.field.kind === "unit" && node.printedUnitKey ? row?.[node.printedUnitKey] : undefined;
    if (typeof printed !== "string" || !printed.trim()) return input;
    return (
      <div className="flex flex-col gap-0.5">
        {input}
        <span className="text-xs text-muted-foreground" data-testid="unit-raw">
          {t("lines.unitRaw", { unit: printed.trim() })}
        </span>
      </div>
    );
  };

  const headerOf = (column: GridColumn) => {
    const name = label(`${node.path}[0].${column.key}`);
    // The amounts are in the document's currency; the cells leave it out so the line fits the panel.
    const currency = column.kind === "money" ? currencyAt(canonical, column.field.currencyPath) : null;
    return currency ? t("lines.inCurrency", { field: name, currency }) : name;
  };

  const lowConfidence = (row: Record<string, unknown>) => columns.some((column) => isLowConfidence(row?.[column.key] as FieldValue | undefined, threshold));

  return (
    <div className="flex flex-col gap-2" data-testid={isLines ? "line-item-grid" : "repeating-group"} data-group={node.path}>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <span className="font-semibold">{isLines ? t("lines.title", { count: rows.length }) : `${group} · ${t("grid.title", { count: rows.length })}`}</span>
        {node.arithmetic &&
          (mismatches > 0 ? (
            <Badge variant="destructive">
              <AlertTriangle aria-hidden />
              {t("lines.mismatches", { count: mismatches })}
            </Badge>
          ) : (
            <Badge variant="success">
              <CheckCircle2 aria-hidden />
              {t("lines.allOk")}
            </Badge>
          ))}
        <span className="ml-auto flex items-center gap-2">
          {optional.length > 0 && (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button type="button" variant="ghost" size="sm" data-testid={`columns-${node.path}`}>
                  <Columns3 aria-hidden />
                  {t("grid.columns")}
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuLabel>{t("grid.columns")}</DropdownMenuLabel>
                <DropdownMenuSeparator />
                {optional.map((column) => (
                  <DropdownMenuCheckboxItem
                    key={column.key}
                    checked={picked.has(column.key)}
                    onSelect={(event) => event.preventDefault()}
                    onCheckedChange={(checked) =>
                      setPicked((previous) => {
                        const next = new Set(previous);
                        if (checked) next.add(column.key);
                        else next.delete(column.key);
                        return next;
                      })
                    }
                  >
                    {label(`${node.path}[0].${column.key}`)}
                  </DropdownMenuCheckboxItem>
                ))}
              </DropdownMenuContent>
            </DropdownMenu>
          )}
          {addButton}
        </span>
      </div>
      <div className="overflow-x-auto rounded-lg border">
        <Table className="[&_td]:px-1.5 [&_th]:px-1.5">
          <TableCaption className="sr-only">{isLines ? t("lines.caption") : t("grid.caption", { group })}</TableCaption>
          <TableHeader>
            {/*
              "#" and "Check" span both header rows and sit at the bottom; pb-1.5 puts their text on the line of the
              second row's labels, which are centred in h-8 (32 px - 20 px line) / 2 = 6 px.
            */}
            <TableRow>
              <TableHead rowSpan={2} className="w-8 pb-1.5 align-bottom">
                {t("fields.line.lineNumber")}
              </TableHead>
              <TableHead colSpan={span}>{wide ? headerOf(wide) : null}</TableHead>
              {(node.arithmetic || canEdit) && (
                <TableHead rowSpan={2} className="w-12 pb-1.5 align-bottom whitespace-nowrap">
                  {node.arithmetic ? t("lines.check") : <span className="sr-only">{t("grid.removeRow", { row: "" })}</span>}
                </TableHead>
              )}
            </TableRow>
            <TableRow>
              {valueColumns.map((column) => (
                <TableHead key={column.key} className={cn("h-8 whitespace-nowrap", (column.kind === "money" || column.kind === "number") && "text-right")}>
                  {headerOf(column)}
                </TableHead>
              ))}
              {node.nested.map((nested) => (
                <TableHead key={nested.key} className="h-8 whitespace-nowrap">
                  {label(`${node.path}[0].${nested.key}`)}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          {rows.slice(first, last).map((row, offset) => {
            const index = first + offset;
            const result = arithmetic[index];
            const rowClass = cn(
              result?.status === "mismatch" && "bg-destructive/5",
              flagged.lines.has(index) && "bg-amber-500/5",
              // Amber like the confidence badge: a low confidence is a warning to check, not a failure.
              lowConfidence(row) && "[&>td:first-child]:border-l-2 [&>td:first-child]:border-l-amber-500",
            );
            return (
              <TableBody key={index} data-line={index} data-arithmetic={result?.status} className="border-b last:border-b-0 [&_tr]:border-b-0">
                <TableRow data-arithmetic={result?.status} className={cn(rowClass, "hover:bg-transparent")}>
                  <TableCell rowSpan={2} className="align-top tabular-nums text-muted-foreground">
                    {lineNumberOf(row, index)}
                  </TableCell>
                  <TableCell colSpan={span} className="pb-1">
                    {wide ? cell(index, row, wide) : null}
                  </TableCell>
                  {(node.arithmetic || canEdit) && (
                    <TableCell rowSpan={2} className="align-top">
                      <div className="flex flex-col items-start gap-1">
                        {node.arithmetic && <ArithmeticIndicator result={result} locale={i18n.language} />}
                        {canEdit && (
                          <Button
                            type="button"
                            variant="ghost"
                            size="icon-sm"
                            aria-label={t("grid.removeRow", { row: lineNumberOf(row, index) })}
                            title={t("grid.removeRow", { row: lineNumberOf(row, index) })}
                            disabled={busy}
                            onClick={() => onStructuralChange?.(removeAt(`${node.path}[${index}]`))}
                          >
                            <Trash2 aria-hidden />
                          </Button>
                        )}
                      </div>
                    </TableCell>
                  )}
                </TableRow>
                <TableRow className={cn(rowClass, "hover:bg-transparent")}>
                  {valueColumns.map((column) => (
                    <Fragment key={column.key}>
                      <TableCell className="pt-0 align-top">{cell(index, row, column)}</TableCell>
                    </Fragment>
                  ))}
                  {node.nested.map((nested) => {
                    const entries = row?.[nested.key];
                    return (
                      <TableCell key={nested.key} className="pt-0 align-top text-sm text-muted-foreground tabular-nums" data-nested={nested.key}>
                        {t("grid.nested", { count: Array.isArray(entries) ? entries.length : 0 })}
                      </TableCell>
                    );
                  })}
                </TableRow>
              </TableBody>
            );
          })}
        </Table>
      </div>
      {paged && (
        <div className="flex items-center justify-end gap-2 text-sm" data-testid="grid-pager">
          <span className="text-muted-foreground" aria-live="polite">
            {t("grid.page", { from: first + 1, to: last, count: rows.length })}
          </span>
          <Button type="button" variant="outline" size="icon-sm" aria-label={t("grid.previousPage")} disabled={current === 0} onClick={() => setPage(current - 1)}>
            <ChevronLeft aria-hidden />
          </Button>
          <Button type="button" variant="outline" size="icon-sm" aria-label={t("grid.nextPage")} disabled={current >= pageCount - 1} onClick={() => setPage(current + 1)}>
            <ChevronRight aria-hidden />
          </Button>
        </div>
      )}
    </div>
  );
}

export function ArithmeticIndicator({ result, locale }: { result: LineArithmeticResult | undefined; locale?: string }) {
  const { t } = useTranslation(REVIEW_NS);
  if (!result || result.status === "incomplete") {
    return (
      <Badge variant="outline" aria-label={t("lines.incomplete")} title={t("lines.incomplete")} data-arithmetic="incomplete">
        <MinusCircle aria-hidden />
        <span className="sr-only">{t("lines.incomplete")}</span>
      </Badge>
    );
  }
  if (result.status === "ok") {
    return (
      <Badge variant="success" aria-label={t("lines.ok")} title={t("lines.okDetail", { expected: formatNumber(result.expected, locale) })} data-arithmetic="ok">
        <CheckCircle2 aria-hidden />
        <span className="sr-only">{t("lines.ok")}</span>
      </Badge>
    );
  }
  const detail = t("lines.mismatchDetail", { expected: formatNumber(result.expected, locale), actual: formatNumber(result.actual, locale) });
  return (
    <Badge variant="destructive" aria-label={detail} title={detail} data-arithmetic="mismatch">
      <AlertTriangle aria-hidden />
      <span className="tabular-nums">{formatNumber(result.difference, locale, { signDisplay: "always", minimumFractionDigits: 2, maximumFractionDigits: 2 })}</span>
    </Badge>
  );
}
