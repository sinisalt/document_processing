import { Fragment } from "react";
import { useTranslation } from "react-i18next";
import { ArrowRight } from "lucide-react";
import { Skeleton } from "@docproc/ui";
import { EmptyState, StatusBadge, formatDateTime } from "@docproc/app-shell";
import { useDocumentEvents } from "@/lib/queries";

/** Actors the pipeline writes (`DocumentTimeline.Actor`); a person's name is shown as stored. */
const SYSTEM_ACTORS = new Set(["pipeline", "system"]);

/** `fromStage` → "From stage": the fallback label of a detail key the UI has no translation for. */
export function humanizeKey(key: string): string {
  const words = key.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").trim().toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** An event's details as label/value pairs: scalars as text, yes/no for flags, lists joined, nested objects as compact JSON. */
export function detailEntries(details: unknown, words: { yes: string; no: string } = { yes: "yes", no: "no" }): Array<[string, string]> {
  if (!details || typeof details !== "object" || Array.isArray(details)) return [];
  return Object.entries(details as Record<string, unknown>)
    .filter(([, value]) => value !== null && value !== undefined && value !== "")
    .map(([key, value]): [string, string] => {
      if (Array.isArray(value)) return [key, value.map((item) => (typeof item === "object" ? JSON.stringify(item) : String(item))).join(", ")];
      if (typeof value === "object") return [key, JSON.stringify(value)];
      if (typeof value === "boolean") return [key, value ? words.yes : words.no];
      return [key, String(value)];
    });
}

/** The Timeline tab: `GET /documents/{id}/events`, oldest first, with each event's details collapsible. */
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
        const details = detailEntries(event.details, { yes: t("review.timeline.yes"), no: t("review.timeline.no") });
        const actor = event.actor && SYSTEM_ACTORS.has(event.actor.toLowerCase()) ? t(`review.timeline.actors.${event.actor.toLowerCase()}`) : event.actor;
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
              <time dateTime={event.at}>{formatDateTime(event.at, i18n.language, { dateStyle: "medium", timeStyle: "medium" })}</time>
              {actor && <> · {actor}</>}
            </div>
            {details.length > 0 && (
              <details className="mt-1">
                <summary className="cursor-pointer text-xs text-muted-foreground">{t("review.timeline.details")}</summary>
                <dl className="mt-1 grid max-h-48 grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 overflow-auto rounded bg-muted p-2 text-xs">
                  {details.map(([key, value]) => (
                    <Fragment key={key}>
                      <dt className="text-muted-foreground">{t(`review.timeline.detailKeys.${key}`, { defaultValue: humanizeKey(key) })}</dt>
                      <dd className="min-w-0 break-words">{value}</dd>
                    </Fragment>
                  ))}
                </dl>
              </details>
            )}
          </li>
        );
      })}
    </ol>
  );
}
