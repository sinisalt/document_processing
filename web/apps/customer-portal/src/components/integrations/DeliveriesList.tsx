import { useMemo, useState, type ComponentProps } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { FileJson, Loader2, RotateCcw } from "lucide-react";
import { Badge, Button, Skeleton, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@docproc/ui";
import { DataTable, createDataTableColumnHelper, formatDateTime, queryKeys, relativeTime, unwrap, useApi } from "@docproc/app-shell";
import { EMPTY_ARRAY } from "@/lib/queries";
import { useDelivery, useDeliveryPayload, type Delivery } from "./api";

const helper = createDataTableColumnHelper<Delivery>();
const COMPACT_COLUMNS = { event: false, documentVersion: false };

/** The badge of a delivery status: green delivered, amber waiting for its retry, red dead-lettered (docs/05 "Shared UI decisions"). */
export function deliveryStatusVariant(status: string): ComponentProps<typeof Badge>["variant"] {
  switch (status) {
    case "Delivered":
      return "success";
    case "Failed":
      return "warning";
    case "DeadLettered":
      return "destructive";
    case "Pending":
    case "Sending":
      return "secondary";
    default:
      return "outline";
  }
}

/** An attempt's class in words: "Will be retried · http" for `retryable:http`, "Delivered" for none. */
export function errorClassText(errorClass: string | null, t: (key: string, options?: Record<string, unknown>) => string): string {
  if (!errorClass) return t("integrations.deliveries.errorClasses.delivered");
  const [kind, ...rest] = errorClass.split(":");
  const label = t(`integrations.deliveries.errorClasses.${kind}`, { defaultValue: kind });
  return rest.join(":") ? `${label} · ${rest.join(":")}` : label;
}

/** The badge of an acknowledgement (docs/11 §13.8): Pending until the receiver answers, then Accepted, Posted or Rejected. */
export function externalStatusVariant(status: string): ComponentProps<typeof Badge>["variant"] {
  switch (status) {
    case "Accepted":
    case "Posted":
      return "success";
    case "Rejected":
      return "destructive";
    default:
      return "secondary";
  }
}

export function ExternalStatusBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge variant={externalStatusVariant(status)} data-external-status={status}>
      {t(`integrations.deliveries.externalStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function DeliveryStatusBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge variant={deliveryStatusVariant(status)} data-status={status}>
      {t(`integrations.deliveries.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export interface DeliveriesListProps {
  deliveries: Delivery[];
  isLoading: boolean;
  query?: { isError: boolean; error: unknown; refetch: () => unknown; isFetching?: boolean };
  /** IntegrationAdmin: offers redelivery. */
  canRedeliver: boolean;
  /** The log of all documents shows which document a delivery belongs to. */
  showDocument?: boolean;
  onOpenDocument?: (documentId: string) => void;
  emptyTitle: string;
  emptyDescription?: string;
}

/**
 * Deliveries as a table (docs/11 §13.6, §19): status, destination, binding, revision, attempts, the last answer and the
 * next retry; a row opens to its attempts (status code, error class, duration, the answer's first 4 KB), the external
 * id the receiver gave back, the payload as sent, and redelivery from the snapshot or rendered again.
 */
export function DeliveriesList({ deliveries, isLoading, query, canRedeliver, showDocument, onOpenDocument, emptyTitle, emptyDescription }: DeliveriesListProps) {
  const { t, i18n } = useTranslation();
  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("createdAt", { header: t("integrations.deliveries.columns.created"), cell: (info) => <span title={formatDateTime(info.getValue(), i18n.language)}>{relativeTime(info.getValue(), i18n.language)}</span>, sortFn: "datetime" }),
        ...(showDocument
          ? [
              helper.accessor("documentId", {
                header: t("integrations.deliveries.columns.document"),
                cell: (info) => (
                  <Button variant="link" size="xs" className="px-0 font-mono" onClick={() => onOpenDocument?.(info.getValue())}>
                    …{info.getValue().slice(-12)}
                  </Button>
                ),
              }),
            ]
          : []),
        helper.accessor("destinationName", { header: t("integrations.deliveries.columns.destination"), cell: ({ row }) => <span className="font-medium">{row.original.destinationName ?? row.original.destinationId}</span> }),
        helper.accessor("bindingName", { header: t("integrations.deliveries.columns.binding"), cell: ({ row }) => `${row.original.bindingName ?? "—"}${row.original.mappingVersion !== null ? ` · v${Number(row.original.mappingVersion)}` : ""}` }),
        helper.accessor("event", { header: t("integrations.deliveries.columns.event"), cell: (info) => t(`integrations.events.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("documentVersion", { header: t("integrations.deliveries.columns.revision"), cell: (info) => Number(info.getValue()) }),
        helper.accessor("status", { header: t("integrations.deliveries.columns.status"), cell: (info) => <DeliveryStatusBadge status={info.getValue()} /> }),
        helper.accessor("attempts", { header: t("integrations.deliveries.columns.attempts"), cell: (info) => Number(info.getValue()) }),
        helper.accessor("lastStatusCode", { header: t("integrations.deliveries.columns.lastStatus"), cell: (info) => (info.getValue() !== null ? Number(info.getValue()) : "—") }),
        helper.display({
          id: "note",
          header: t("integrations.deliveries.columns.note"),
          cell: ({ row }) => {
            const d = row.original;
            if (d.status === "Failed" && d.nextAttemptAt) return <span className="text-xs">{t("integrations.deliveries.nextAttempt", { when: relativeTime(d.nextAttemptAt, i18n.language) })}</span>;
            if (d.status === "Delivered" && d.deliveredAt) return <span className="text-xs text-muted-foreground">{t("integrations.deliveries.deliveredAt", { when: formatDateTime(d.deliveredAt, i18n.language) })}</span>;
            return d.lastError ? <span className="line-clamp-2 text-xs text-muted-foreground">{d.lastError}</span> : null;
          },
        }),
      ]),
    [t, i18n.language, showDocument, onOpenDocument],
  );
  return (
    <DataTable
      columns={columns}
      data={deliveries.length ? deliveries : EMPTY_ARRAY}
      getRowId={(row) => row.id}
      isLoading={isLoading}
      query={query}
      caption={t("integrations.deliveries.title")}
      emptyTitle={emptyTitle}
      emptyDescription={emptyDescription}
      // In the document's tab (beside the page viewer) the event and revision columns start hidden; the column menu shows them.
      initialColumnVisibility={showDocument ? undefined : COMPACT_COLUMNS}
      enableColumnVisibility={!showDocument}
      renderSubRow={(row) => <DeliveryDetailPanel delivery={row} canRedeliver={canRedeliver} />}
      rowAttributes={(row) => ({ "data-delivery": row.id, "data-status": row.status, "data-destination": row.destinationName ?? undefined })}
    />
  );
}

function DeliveryDetailPanel({ delivery, canRedeliver }: { delivery: Delivery; canRedeliver: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const detail = useDelivery(delivery.id, true);
  const [showPayload, setShowPayload] = useState(false);
  const hasPayload = delivery.payloadSha256 !== null;
  const payload = useDeliveryPayload(delivery.id, showPayload && hasPayload);

  const redeliver = useMutation({
    mutationFn: async (mode: "snapshot" | "rerender") => unwrap(await client.POST("/api/v1/deliveries/{id}:redeliver", { params: { path: { id: delivery.id } }, body: { mode, mappingVersion: null } })),
    onSuccess: async (created) => {
      toast.success(t("integrations.deliveries.redelivered", { key: created.idempotencyKey }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.deliveries.all });
      await queryClient.invalidateQueries({ queryKey: queryKeys.documents.events(delivery.documentId) });
    },
  });

  return (
    <div className="grid gap-3 p-2 text-sm" data-testid="delivery-detail">
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
        <dt className="text-muted-foreground">{t("integrations.deliveries.idempotencyKey")}</dt>
        <dd className="font-mono break-all">{delivery.idempotencyKey}</dd>
        {delivery.feedSequence !== null && (
          <>
            <dt className="text-muted-foreground">{t("integrations.deliveries.feedSequence")}</dt>
            <dd className="font-mono">{Number(delivery.feedSequence)}</dd>
          </>
        )}
        {delivery.redeliveryOfId && (
          <>
            <dt className="text-muted-foreground">{t("integrations.deliveries.redeliveryOf")}</dt>
            <dd className="font-mono">{delivery.redeliveryOfId}</dd>
          </>
        )}
        {detail.data?.externalRef && (
          <>
            <dt className="text-muted-foreground">{t("integrations.deliveries.externalId")}</dt>
            <dd className="font-mono" data-testid="delivery-external-id">
              {detail.data.externalRef.externalId}
            </dd>
            <dt className="text-muted-foreground">{t("integrations.deliveries.acknowledgement")}</dt>
            <dd className="flex flex-wrap items-center gap-2" data-testid="delivery-acknowledgement">
              <ExternalStatusBadge status={detail.data.externalRef.status} />
              <span className="text-muted-foreground">{formatDateTime(detail.data.externalRef.updatedAt, i18n.language)}</span>
              {detail.data.externalRef.message && <span>{detail.data.externalRef.message}</span>}
            </dd>
          </>
        )}
        {delivery.lastError && (
          <>
            <dt className="text-muted-foreground">{t("integrations.deliveries.lastError")}</dt>
            <dd>{delivery.lastError}</dd>
          </>
        )}
      </dl>
      {detail.isPending ? (
        <Skeleton className="h-16" />
      ) : (detail.data?.attempts.length ?? 0) === 0 ? (
        <p className="text-xs text-muted-foreground">{t("integrations.deliveries.noAttempts")}</p>
      ) : (
        <Table data-testid="delivery-attempts">
          <TableHeader>
            <TableRow>
              <TableHead>{t("integrations.deliveries.attemptColumns.at")}</TableHead>
              <TableHead>{t("integrations.deliveries.attemptColumns.status")}</TableHead>
              <TableHead>{t("integrations.deliveries.attemptColumns.class")}</TableHead>
              <TableHead>{t("integrations.deliveries.attemptColumns.duration")}</TableHead>
              <TableHead>{t("integrations.deliveries.attemptColumns.response")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {detail.data!.attempts.map((attempt, i) => (
              <TableRow key={i} data-attempt-status={attempt.statusCode !== null ? Number(attempt.statusCode) : "none"}>
                <TableCell className="text-xs whitespace-nowrap">{formatDateTime(attempt.at, i18n.language)}</TableCell>
                <TableCell className="font-mono text-xs">{attempt.statusCode !== null ? Number(attempt.statusCode) : "—"}</TableCell>
                <TableCell className="text-xs">{errorClassText(attempt.errorClass, t)}</TableCell>
                <TableCell className="text-xs">{t("integrations.preview.elapsed", { ms: Number(attempt.durationMs) })}</TableCell>
                <TableCell className="max-w-md">
                  {attempt.responseExcerpt ? <pre className="max-h-32 overflow-auto rounded bg-muted p-1.5 font-mono text-[10px] whitespace-pre-wrap">{attempt.responseExcerpt}</pre> : "—"}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      <div className="flex flex-wrap gap-2">
        {hasPayload && (
          <Button size="xs" variant="outline" onClick={() => setShowPayload((v) => !v)} data-testid="delivery-show-payload">
            <FileJson aria-hidden />
            {showPayload ? t("integrations.deliveries.hidePayload") : t("integrations.deliveries.showPayload")}
          </Button>
        )}
        {canRedeliver && delivery.status !== "Sending" && (
          <>
            <Button size="xs" variant="outline" onClick={() => redeliver.mutate("snapshot")} disabled={redeliver.isPending || !hasPayload} data-testid="delivery-redeliver">
              {redeliver.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <RotateCcw aria-hidden />}
              {t("integrations.deliveries.redeliverSnapshot")}
            </Button>
            <Button size="xs" variant="ghost" onClick={() => redeliver.mutate("rerender")} disabled={redeliver.isPending} data-testid="delivery-rerender">
              {t("integrations.deliveries.redeliverRerender")}
            </Button>
          </>
        )}
      </div>
      {showPayload &&
        (payload.isPending ? (
          <Skeleton className="h-24" />
        ) : (
          <pre className="max-h-80 overflow-auto rounded-md bg-muted p-2 font-mono text-[11px]" data-testid="delivery-payload">
            {payload.data?.text.slice(0, 200_000)}
          </pre>
        ))}
    </div>
  );
}
