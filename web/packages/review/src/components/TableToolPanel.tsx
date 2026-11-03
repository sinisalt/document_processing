import { useTranslation } from "react-i18next";
import { Loader2, Merge, Table2, Upload, X } from "lucide-react";
import { Alert, AlertDescription, Button, Checkbox, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import { REVIEW_NS, humanize, localized } from "../lib/labels";
import type { FormSpecField } from "../lib/formSpec";
import { mappingProblem, targetOf, tierQuantityOf, type ColumnTarget, type TableReport, type TableRejectedLine, type TableToolState } from "../lib/tableTool";
import { TablePageStrip } from "./TablePageStrip";

export interface TableToolPanelProps {
  /** Null until the reviewer has drawn the table on a page. */
  state: TableToolState | null;
  /** The row fields a column can fill. */
  rowFields: readonly FormSpecField[];
  /** Whether the rows have tier prices (`tiers`), so a column can be a tier. */
  hasTiers: boolean;
  /** Whether the rows have a `category`, so group headings can fill it. */
  hasCategory: boolean;
  /** The last report: of the proposal (page drawn on), the preview, or the stored write. */
  report: TableReport | null;
  /** Whether the report is the stored one (written), the preview of the mapping, a preview the mapping changed since, or the proposal. */
  reportKind: "proposal" | "preview" | "stale" | "stored" | null;
  currentPage: number;
  proposing?: boolean;
  previewing?: boolean;
  writing?: boolean;
  /** Writing is allowed (the reviewer may correct the document). */
  canWrite: boolean;
  error?: string | null;
  onColumnTarget: (index: number, target: ColumnTarget) => void;
  onRemoveDivider: (index: number) => void;
  onAllPagesChange: (allPages: boolean) => void;
  onGroupHeadingsChange: (on: boolean) => void;
  onPreview: () => void;
  onWrite: () => void;
  onReset: () => void;
  onPickPage: (page: number) => void;
  onPickRejected?: (line: TableRejectedLine) => void;
}

const NONE = "__none__";
const TIER = "__tier__";

/**
 * The table tool's side of the review screen (docs/11 §12.4): what to do on the page, the columns with the field each
 * fills, "apply to all pages", group headings, and the page strip of the report; Preview reads the pages, Write stores
 * every row as one correction. The dividers themselves are drawn and moved on the page.
 */
export function TableToolPanel(props: TableToolPanelProps) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  const { state, rowFields, report } = props;
  const busy = !!props.proposing || !!props.previewing || !!props.writing;
  const problem = state ? mappingProblem(state) : null;
  const labelOf = (field: FormSpecField) => localized(field.label, i18n.language) ?? humanize(field.key);

  if (!state) {
    return (
      <div className="flex flex-col gap-3" data-testid="table-tool">
        <Alert>
          <Table2 aria-hidden />
          <AlertDescription>{props.proposing ? t("table.proposing") : t("table.drawHint")}</AlertDescription>
        </Alert>
        {props.error && <p className="text-sm text-destructive" role="alert">{props.error}</p>}
        {report && props.reportKind === "stored" && <TablePageStrip report={report} currentPage={props.currentPage} onPickPage={props.onPickPage} onPickRejected={props.onPickRejected} />}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4" data-testid="table-tool" data-page={state.page}>
      <p className="text-sm text-muted-foreground">{t("table.columnsHint", { page: state.page })}</p>
      <ol className="flex flex-col gap-2" aria-label={t("table.columns")}>
        {state.columns.map((column, index) => {
          const target = targetOf(column);
          const value = target.kind === "field" ? target.key : target.kind === "tier" ? TIER : NONE;
          const id = `table-column-${index}`;
          return (
            <li key={index} className="flex flex-wrap items-center gap-2" data-testid={`table-column-${index}`} data-field={column.field ?? ""}>
              <Label htmlFor={id} className="w-28 shrink-0 truncate text-xs" title={column.header ?? undefined}>
                {column.header ? `“${column.header}”` : t("table.columnN", { n: index + 1 })}
              </Label>
              <Select
                value={value}
                onValueChange={(next) =>
                  props.onColumnTarget(index, next === NONE ? { kind: "none" } : next === TIER ? { kind: "tier", minQuantity: tierQuantityOf(column.header) ?? Number(column.tierMinQuantity ?? 1) } : { kind: "field", key: next })
                }
                disabled={busy}
              >
                <SelectTrigger id={id} size="sm" className="min-w-40 flex-1">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={NONE}>{t("table.ignore")}</SelectItem>
                  {rowFields.map((field) => (
                    <SelectItem key={field.key} value={field.key}>
                      {labelOf(field)}
                    </SelectItem>
                  ))}
                  {props.hasTiers && <SelectItem value={TIER}>{t("table.tier", { quantity: tierQuantityOf(column.header) ?? column.tierMinQuantity ?? "?" })}</SelectItem>}
                </SelectContent>
              </Select>
              {index > 0 && (
                <Button type="button" variant="ghost" size="icon-sm" aria-label={t("table.merge", { n: index + 1 })} title={t("table.merge", { n: index + 1 })} disabled={busy} onClick={() => props.onRemoveDivider(index)}>
                  <Merge aria-hidden />
                </Button>
              )}
            </li>
          );
        })}
      </ol>
      <p className="text-xs text-muted-foreground">{t("table.dividerHint")}</p>
      <div className="flex flex-col gap-2">
        <div className="flex items-center gap-2">
          <Checkbox id="table-all-pages" checked={state.allPages} onCheckedChange={(checked) => props.onAllPagesChange(checked === true)} disabled={busy} />
          <Label htmlFor="table-all-pages">{t("table.allPages")}</Label>
        </div>
        {props.hasCategory && (
          <div className="flex items-center gap-2">
            <Checkbox id="table-group-headings" checked={state.groupHeadingTarget !== null} onCheckedChange={(checked) => props.onGroupHeadingsChange(checked === true)} disabled={busy} />
            <Label htmlFor="table-group-headings">{t("table.groupHeadings")}</Label>
          </div>
        )}
      </div>
      {problem && (
        <p className="text-sm text-destructive" role="alert" data-testid="table-problem">
          {t(`table.problems.${problem}`)}
        </p>
      )}
      {props.error && (
        <p className="text-sm text-destructive" role="alert">
          {props.error}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="secondary" onClick={props.onPreview} disabled={busy || problem !== null} data-testid="table-preview">
          {props.previewing ? <Loader2 className="animate-spin" aria-hidden /> : <Table2 aria-hidden />}
          {state.allPages ? t("table.previewAll") : t("table.preview")}
        </Button>
        {props.canWrite && (
          <Button type="button" onClick={props.onWrite} disabled={busy || problem !== null || props.reportKind !== "preview" || Number(report?.rows ?? 0) === 0} data-testid="table-write">
            {props.writing ? <Loader2 className="animate-spin" aria-hidden /> : <Upload aria-hidden />}
            {t("table.write", { count: Number(report?.rows ?? 0) })}
          </Button>
        )}
        <Button type="button" variant="ghost" onClick={props.onReset} disabled={busy} data-testid="table-reset">
          <X aria-hidden />
          {t("table.reset")}
        </Button>
      </div>
      {report && (
        <div className="flex flex-col gap-1">
          <p className="text-xs font-medium text-muted-foreground">{props.reportKind === "stored"
              ? t("table.reportStored")
              : props.reportKind === "preview"
                ? t("table.reportPreview")
                : props.reportKind === "stale"
                  ? t("table.reportStale")
                  : t("table.reportProposal", { page: state.page })}</p>
          <TablePageStrip report={report} currentPage={props.currentPage} onPickPage={props.onPickPage} onPickRejected={props.onPickRejected} />
        </div>
      )}
    </div>
  );
}
