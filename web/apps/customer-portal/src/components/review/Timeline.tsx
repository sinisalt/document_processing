import { useTranslation } from "react-i18next";
import { ArrowRight } from "lucide-react";
import { Skeleton } from "@docproc/ui";
import { EmptyState, StatusBadge, formatDateTime } from "@docproc/app-shell";
import { useDocumentEvents } from "@/lib/queries";

/** The Timeline tab: `GET /documents/{id}/events`, oldest first, with the raw details collapsible. */
export function Timeline({ documentId, enabled }: { documentId: string; enabled: boolean }) {
  const { t, i18n } = useTranslation();
  const events = useDocumentEvents(documentId, enabled);
  if (events.isPending) {
    return (
      <div className="flex flex-col gap-2" aria-busy>
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-10" />
        ))}
      </div>
    );
  }
  if (!events.data || events.data.length === 0) {
    return <EmptyState title={t("review.timeline.empty")} />;
  }
  return (
    <ol className="relative flex flex-col gap-3 border-l pl-4" aria-label={t("review.timeline.title")} data-testid="timeline">
      {events.data.map((event) => {
        const details = event.details && typeof event.details === "object" && Object.keys(event.details as object).length > 0 ? JSON.stringify(event.details, null, 2) : null;
        return (
          <li key={String(event.id)} className="relative text-sm">
            <span className="absolute top-1.5 -left-[21px] size-2.5 rounded-full border-2 border-background bg-primary" aria-hidden />
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium">{t(`events.${event.eventType}`, { defaultValue: event.eventType })}</span>
              {event.fromStatus && event.toStatus && event.fromStatus !== event.toStatus && (
                <span className="flex items-center gap-1">
                  <StatusBadge status={event.fromStatus} />
                  <ArrowRight className="size-3 text-muted-foreground" aria-hidden />
                  <StatusBadge status={event.toStatus} />
                </span>
              )}
            </div>
            <div className="text-xs text-muted-foreground">
              <time dateTime={event.at}>{formatDateTime(event.at, i18n.language)}</time>
              {event.actor && <> · {event.actor}</>}
            </div>
            {details && (
              <details className="mt-1">
                <summary className="cursor-pointer text-xs text-muted-foreground">{t("review.timeline.details")}</summary>
                <pre className="mt-1 max-h-48 overflow-auto rounded bg-muted p-2 text-xs">{details}</pre>
              </details>
            )}
          </li>
        );
      })}
    </ol>
  );
}
