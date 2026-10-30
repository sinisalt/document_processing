import { Fragment, useState } from "react";
import { useTranslation } from "react-i18next";
import { ChevronDown, ChevronRight } from "lucide-react";
import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, cn } from "@docproc/ui";
import { EmptyState, StatusBadge, formatDateTime } from "@docproc/app-shell";
import { detailRows, eventKind, eventStage, failureKinds, humanKinds, summarizeEvent, type DocumentEvent } from "@/lib/timeline";
import { JsonBlock } from "./KeyValue";

export interface TimelineTableProps {
  events: readonly DocumentEvent[];
  isLoading?: boolean;
}

/**
 * The job inspector's timeline: one row per `DocumentEventDto`, oldest first as the API sends them, with the stage,
 * the status transition, the actor and a one-line summary; a row expands into the details JSON as a key/value list
 * plus the raw document.
 */
export function TimelineTable({ events, isLoading = false }: TimelineTableProps) {
  const { t, i18n } = useTranslation("admin");
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const toggle = (id: string) =>
    setOpen((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  if (!isLoading && events.length === 0) {
    return <EmptyState title={t("timeline.empty")} description={t("timeline.emptyDescription")} />;
  }

  return (
    <div className="overflow-hidden rounded-lg border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead className="w-8">
              <span className="sr-only">{t("timeline.details")}</span>
            </TableHead>
            <TableHead>{t("timeline.at")}</TableHead>
            <TableHead>{t("timeline.event")}</TableHead>
            <TableHead>{t("timeline.stage")}</TableHead>
            <TableHead>{t("timeline.transition")}</TableHead>
            <TableHead>{t("timeline.actor")}</TableHead>
            <TableHead>{t("timeline.summary")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {events.map((event) => {
            const id = String(event.id);
            const kind = eventKind(event.eventType);
            const failed = failureKinds.includes(kind);
            const human = humanKinds.includes(kind);
            const stage = eventStage(event);
            const summary = summarizeEvent(event);
            const expanded = open.has(id);
            const hasDetails = event.details !== null && event.details !== undefined;
            const rows = expanded ? detailRows(event.details) : [];
            return (
              <Fragment key={id}>
                <TableRow data-event-type={event.eventType} className={cn(failed && "bg-destructive/5")}>
                  <TableCell className="py-1">
                    {hasDetails && (
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        aria-expanded={expanded}
                        aria-controls={`event-details-${id}`}
                        aria-label={expanded ? t("timeline.hideDetails") : t("timeline.showDetails")}
                        onClick={() => toggle(id)}
                      >
                        {expanded ? <ChevronDown aria-hidden /> : <ChevronRight aria-hidden />}
                      </Button>
                    )}
                  </TableCell>
                  <TableCell className="whitespace-nowrap tabular-nums">{formatDateTime(event.at, i18n.language, { dateStyle: "medium", timeStyle: "medium" })}</TableCell>
                  <TableCell>
                    <Badge variant={failed ? "destructive" : human ? "default" : "secondary"} data-event-kind={kind}>
                      {t(`timeline.eventType.${kind}`, { defaultValue: kind })}
                    </Badge>
                  </TableCell>
                  <TableCell>{stage ?? "—"}</TableCell>
                  <TableCell>
                    {event.fromStatus || event.toStatus ? (
                      <span className="inline-flex flex-wrap items-center gap-1">
                        {event.fromStatus ? <StatusBadge status={event.fromStatus} /> : <span className="text-muted-foreground">—</span>}
                        <span aria-hidden className="text-muted-foreground">
                          →
                        </span>
                        {event.toStatus ? <StatusBadge status={event.toStatus} /> : <span className="text-muted-foreground">—</span>}
                      </span>
                    ) : (
                      "—"
                    )}
                  </TableCell>
                  <TableCell className="whitespace-nowrap">{event.actor ?? "—"}</TableCell>
                  <TableCell className="max-w-md truncate" title={summary ?? undefined}>
                    {summary ?? "—"}
                  </TableCell>
                </TableRow>
                {expanded && (
                  <TableRow id={`event-details-${id}`} className="bg-muted/30 hover:bg-muted/30">
                    <TableCell colSpan={7} className="p-4">
                      <div className="grid gap-4 lg:grid-cols-2">
                        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm" aria-label={t("timeline.details")}>
                          {rows.map((row) => (
                            <Fragment key={row.path}>
                              <dt className="font-mono text-xs text-muted-foreground">{row.path}</dt>
                              <dd className={cn("break-words", row.kind !== "string" && "font-mono text-xs")}>{row.value}</dd>
                            </Fragment>
                          ))}
                        </dl>
                        <JsonBlock value={event.details} />
                      </div>
                    </TableCell>
                  </TableRow>
                )}
              </Fragment>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}
