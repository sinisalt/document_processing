import { Fragment, useMemo } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { Badge, cn } from "@docproc/ui";
import { DataTable, StatusBadge, createDataTableColumnHelper, formatDateTime, type DataTableColumns, type DataTableQueryState } from "@docproc/app-shell";
import { detailRows, eventKind, eventStage, failureKinds, humanKinds, summarizeEvent, type DocumentEvent } from "@/lib/timeline";
import { describeFailure } from "@/lib/failure";
import { JsonBlock } from "./KeyValue";

export interface TimelineTableProps {
  events: readonly DocumentEvent[];
  isLoading?: boolean;
  /** The events query: when it failed the table says so (with Retry) instead of "No events". */
  query?: DataTableQueryState;
}

const helper = createDataTableColumnHelper<DocumentEvent>();

function hasDetails(event: DocumentEvent): boolean {
  return event.details !== null && event.details !== undefined;
}

/** A failure reason with its `DocumentProcessingFailure` code translated (`CorruptPdf` → "Unreadable PDF"). */
export function failureText(reason: string, t: TFunction<"admin">): string {
  return describeFailure(reason, (code) => t(`failure.${code}`, { defaultValue: code }));
}

/** The keys the pipeline writes into `details` (FailureRecorder, PublishHandler, the review and reprocess events). */
const KNOWN_DETAIL_KEYS = new Set(["stage", "fromStage", "attempt", "error", "retry", "reason", "message", "code", "eventType", "outboxMessageId"]);

/**
 * The expanded panel: the details as labelled rows in the UI language (stage names, yes/no, the failure reason with
 * its code translated), with the raw JSON one click away for whoever needs the exact document.
 */
function EventDetails({ event }: { event: DocumentEvent }) {
  const { t } = useTranslation("admin");
  const rows = detailRows(event.details);
  const value = (path: string, raw: string, kind: string) => {
    if (kind === "boolean") return raw === "true" ? t("yes") : t("no");
    if ((path === "stage" || path === "fromStage") && kind === "string") return t(`stages.${raw}`, { defaultValue: raw });
    if (path === "error" && kind === "string") return failureText(raw, t);
    if (path === "reason" && kind === "string") return t(`flagCodes.${raw}`, { defaultValue: raw });
    return raw;
  };
  return (
    <div className="flex flex-col gap-3">
      <dl className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-x-6 gap-y-1 text-sm" aria-label={t("timeline.details")}>
        {rows.map((row) => {
          const known = KNOWN_DETAIL_KEYS.has(row.path);
          return (
            <Fragment key={row.path}>
              <dt className={cn("text-muted-foreground", !known && "font-mono text-xs")}>{known ? t(`timeline.detailKey.${row.path}`) : row.path}</dt>
              <dd className={cn("min-w-0 whitespace-normal break-words", row.kind === "number" ? "tabular-nums" : row.kind !== "string" && row.kind !== "boolean" && "font-mono text-xs")}>{value(row.path, row.value, row.kind)}</dd>
            </Fragment>
          );
        })}
      </dl>
      <details className="group text-sm">
        <summary className="w-fit cursor-pointer text-muted-foreground hover:text-foreground">{t("timeline.rawJson")}</summary>
        <div className="mt-2">
          <JsonBlock value={event.details} />
        </div>
      </details>
    </div>
  );
}

/**
 * The job inspector's timeline on the shell's DataTable: one row per `DocumentEventDto`, oldest first as the API sends
 * them, with the stage, the status transition, the actor and a one-line summary. It sorts and hides columns like the
 * other grids; a row with details expands into the details JSON as a key/value list plus the raw document.
 */
export function TimelineTable({ events, isLoading = false, query }: TimelineTableProps) {
  const { t, i18n } = useTranslation("admin");

  const columns = useMemo<DataTableColumns<DocumentEvent>>(
    () =>
      helper.columns([
        helper.accessor("at", {
          header: t("timeline.at"),
          sortFn: "datetime",
          cell: (info) => <span className="whitespace-nowrap tabular-nums">{formatDateTime(info.getValue(), i18n.language, { dateStyle: "medium", timeStyle: "medium" })}</span>,
        }),
        helper.accessor((event) => eventKind(event.eventType), {
          id: "event",
          header: t("timeline.event"),
          cell: (info) => {
            const kind = info.getValue();
            const variant = failureKinds.includes(kind) ? "destructive" : humanKinds.includes(kind) ? "default" : "secondary";
            return (
              <Badge variant={variant} data-event-kind={kind}>
                {t(`timeline.eventType.${kind}`, { defaultValue: kind })}
              </Badge>
            );
          },
        }),
        helper.accessor((event) => eventStage(event) ?? "", { id: "stage", header: t("timeline.stage"), cell: (info) => (info.getValue() ? t(`stages.${info.getValue()}`, { defaultValue: info.getValue() }) : "—") }),
        helper.accessor((event) => event.toStatus ?? event.fromStatus ?? "", {
          id: "transition",
          header: t("timeline.transition"),
          cell: (info) => {
            const { fromStatus, toStatus } = info.row.original;
            if (!fromStatus && !toStatus) return "—";
            return (
              <span className="inline-flex items-center gap-1 whitespace-nowrap">
                {fromStatus ? <StatusBadge status={fromStatus} /> : <span className="text-muted-foreground">—</span>}
                <span aria-hidden className="text-muted-foreground">
                  →
                </span>
                {toStatus ? <StatusBadge status={toStatus} /> : <span className="text-muted-foreground">—</span>}
              </span>
            );
          },
        }),
        helper.accessor((event) => event.actor ?? "", { id: "actor", header: t("timeline.actor"), cell: (info) => <span className="whitespace-nowrap">{info.getValue() || "—"}</span> }),
        helper.accessor((event) => {
          const summary = summarizeEvent(event) ?? "";
          return failureKinds.includes(eventKind(event.eventType)) ? failureText(summary, t) : summary;
        }, {
          id: "summary",
          header: t("timeline.summary"),
          enableSorting: false,
          cell: (info) => (
            <span className="block max-w-sm truncate" title={info.getValue() || undefined}>
              {info.getValue() || "—"}
            </span>
          ),
        }),
      ]),
    [t, i18n.language],
  );

  return (
    <DataTable
      columns={columns}
      data={events as DocumentEvent[]}
      getRowId={(event) => String(event.id)}
      isLoading={isLoading}
      query={query}
      caption={t("timeline.title")}
      emptyTitle={t("timeline.empty")}
      emptyDescription={t("timeline.emptyDescription")}
      enableColumnVisibility
      renderSubRow={(event) => <EventDetails event={event} />}
      getRowCanExpand={hasDetails}
      rowAttributes={(event) => ({ "data-event-type": event.eventType })}
      rowClassName={(event) => (failureKinds.includes(eventKind(event.eventType)) ? "bg-destructive/5" : undefined)}
    />
  );
}
