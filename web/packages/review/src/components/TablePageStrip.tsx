import { useTranslation } from "react-i18next";
import { AlertTriangle, EyeOff } from "lucide-react";
import { Badge, Button, cn } from "@docproc/ui";
import { REVIEW_NS } from "../lib/labels";
import { pageReport, rejectedLineText, reportTotals, stripEntries, type TableRejectedLine, type TableReport } from "../lib/tableTool";

export interface TablePageStripProps {
  report: TableReport;
  /** The page shown in the viewer, marked in the strip. */
  currentPage?: number;
  /** Clicking a page jumps the viewer to it (its dividers are drawn there). */
  onPickPage?: (page: number) => void;
  /** Clicking a rejected line shows its page and box. */
  onPickRejected?: (line: TableRejectedLine) => void;
  /** "Leave out lines like this": the text of a rejected line (its last line) becomes a skip line. */
  onSkipLine?: (text: string) => void;
  className?: string;
}

/**
 * The table report as a page strip (docs/11 §12.4): the totals, then one chip per page read ("p. 1: 22 rows",
 * "p. 7: 2 rejected", "p. 12: no rows"), and the rejected lines of the current page with their text and reason.
 */
export function TablePageStrip({ report, currentPage, onPickPage, onPickRejected, onSkipLine, className }: TablePageStripProps) {
  const { t } = useTranslation(REVIEW_NS);
  const totals = reportTotals(report);
  const entries = stripEntries(report);
  const rejectedHere = currentPage === undefined ? [] : (pageReport(report, currentPage)?.rejectedLines ?? []);
  const rejectedCount = currentPage === undefined ? 0 : Number(pageReport(report, currentPage)?.rejected ?? 0);
  return (
    <section className={cn("flex flex-col gap-2", className)} aria-label={t("table.strip.label")} data-testid="table-page-strip">
      <p className="text-sm" data-testid="table-totals" data-rows={totals.rows} data-rejected={totals.rejected}>
        {t("table.strip.totals", { rows: totals.rows, rejected: totals.rejected, pages: totals.pages })}
        {totals.pagesWithoutRows > 0 && <span className="ml-1 text-muted-foreground">{t("table.strip.pagesWithoutRows", { count: totals.pagesWithoutRows })}</span>}
      </p>
      <ol className="flex flex-wrap gap-1" aria-label={t("table.strip.pages")}>
        {entries.map((entry) => (
          <li key={entry.page}>
            <Button
              type="button"
              size="sm"
              variant={entry.page === currentPage ? "default" : "outline"}
              className={cn("h-7 gap-1 px-2 text-xs tabular-nums", entry.status === "rejected" && entry.page !== currentPage && "border-destructive text-destructive", entry.status === "empty" && entry.page !== currentPage && "text-muted-foreground")}
              aria-current={entry.page === currentPage ? "page" : undefined}
              onClick={() => onPickPage?.(entry.page)}
              data-testid={`strip-page-${entry.page}`}
              data-rows={entry.rows}
              data-rejected={entry.rejected}
              title={entry.headerFound ? t("table.strip.headerFound") : t("table.strip.headerReused")}
            >
              {entry.status === "empty"
                ? t("table.strip.noRows", { page: entry.page })
                : entry.status === "rejected"
                  ? t("table.strip.rejected", { page: entry.page, rows: entry.rows, count: entry.rejected })
                  : t("table.strip.rows", { page: entry.page, count: entry.rows })}
            </Button>
          </li>
        ))}
      </ol>
      {rejectedCount > 0 && (
        <div className="rounded-md border border-destructive/40 p-2" data-testid="table-rejected">
          <p className="mb-1 flex items-center gap-1 text-xs font-medium text-destructive">
            <AlertTriangle className="size-3.5" aria-hidden />
            {t("table.strip.rejectedHeading", { page: currentPage, count: rejectedCount })}
          </p>
          <ul className="flex flex-col gap-1">
            {rejectedHere.map((line, index) => (
              <li key={index} className="flex items-start gap-1">
                <button type="button" className="min-w-0 flex-1 rounded px-1 text-left text-xs hover:bg-muted" onClick={() => onPickRejected?.(line)}>
                  <span className="font-mono">{line.text}</span>
                  <Badge variant="outline" className="ml-2">
                    {t(`table.reasons.${line.reason}`, { defaultValue: line.reason })}
                  </Badge>
                  <span className="ml-1 text-muted-foreground">{line.message}</span>
                </button>
                {onSkipLine && (
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    aria-label={t("table.notRows.skipThis", { text: rejectedLineText(line) })}
                    title={t("table.notRows.skipThis", { text: rejectedLineText(line) })}
                    onClick={() => onSkipLine(rejectedLineText(line))}
                    data-testid="table-skip-rejected"
                  >
                    <EyeOff aria-hidden />
                  </Button>
                )}
              </li>
            ))}
          </ul>
          {rejectedCount > rejectedHere.length && <p className="mt-1 text-xs text-muted-foreground">{t("table.strip.moreRejected", { count: rejectedCount - rejectedHere.length })}</p>}
        </div>
      )}
    </section>
  );
}
