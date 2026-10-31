import { Fragment, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Badge, cn } from "@docproc/ui";
import { DataTable, StatusBadge, createDataTableColumnHelper, formatDateTime, type DataTableColumns, type DataTableQueryState } from "@docproc/app-shell";
import { detailRows, eventKind, eventStage, failureKinds, humanKinds, summarizeEvent, type DocumentEvent } from "@/lib/timeline";
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

/** The expanded panel: the details JSON as a key/value list next to the raw document. */
function EventDetails({ event }: { event: DocumentEvent }) {
  const { t } = useTranslation("admin");
  const rows = detailRows(event.details);
  return (
    <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <dl className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm" aria-label={t("timeline.details")}>
        {rows.map((row) => (
          <Fragment key={row.path}>
            <dt className="font-mono text-xs text-muted-foreground">{row.path}</dt>
            <dd className={cn("min-w-0 whitespace-normal break-words", row.kind !== "string" && "font-mono text-xs")}>{row.value}</dd>
          </Fragment>
        ))}
      </dl>
      <JsonBlock value={event.details} />
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
        helper.accessor((event) => eventStage(event) ?? "", { id: "stage", header: t("timeline.stage"), cell: (info) => info.getValue() || "—" }),
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
        helper.accessor((event) => summarizeEvent(event) ?? "", {
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
