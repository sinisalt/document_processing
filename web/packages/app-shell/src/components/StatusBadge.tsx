import { useTranslation } from "react-i18next";
import type { components } from "@docproc/api-client";
import { Badge, cn } from "@docproc/ui";
import type { BadgeVariant } from "../lib/fieldValue";

export type DocumentStatus = components["schemas"]["DocumentStatus"];
export type ReviewTaskStatus = components["schemas"]["ReviewTaskStatus"];
export type TenantStatus = "Active" | "Suspended" | "Closed";

/**
 * Settled states read calm, a document waiting for a reviewer reads amber (work to do, not an error), only a failure
 * reads red, in-flight states read neutral.
 */
export const documentStatusVariant: Readonly<Record<DocumentStatus, BadgeVariant>> = {
  Received: "outline",
  Normalized: "outline",
  Split: "outline",
  Classified: "outline",
  Matched: "outline",
  Extracted: "outline",
  Validated: "secondary",
  AutoApproved: "default",
  NeedsReview: "warning",
  Approved: "default",
  Rejected: "secondary",
  Failed: "destructive",
};

/** An open task is the queue's normal state: amber like the document it belongs to, never the red of a failure. */
export const reviewTaskStatusVariant: Readonly<Record<ReviewTaskStatus, BadgeVariant>> = {
  Open: "warning",
  InProgress: "default",
  Done: "secondary",
  Skipped: "outline",
};

export const tenantStatusVariant: Readonly<Record<TenantStatus, BadgeVariant>> = {
  Active: "default",
  Suspended: "destructive",
  Closed: "outline",
};

/** States in which the pipeline is still writing the document (docs/03 state machine). */
export const inFlightStatuses: readonly DocumentStatus[] = ["Received", "Normalized", "Split", "Classified", "Matched", "Extracted"];

export function isInFlight(status: DocumentStatus): boolean {
  return inFlightStatuses.includes(status);
}

/** Where `PATCH /documents/{id}/fields` is allowed (docs/05). */
export function isCorrectable(status: DocumentStatus): boolean {
  return status === "NeedsReview" || status === "Validated" || status === "Failed";
}

export interface StatusBadgeProps {
  status: DocumentStatus;
  className?: string;
}

export function StatusBadge({ status, className }: StatusBadgeProps) {
  const { t } = useTranslation("common");
  return (
    <Badge variant={documentStatusVariant[status] ?? "outline"} className={cn(isInFlight(status) && "animate-pulse", className)} data-status={status}>
      {t(`documentStatus.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function ReviewTaskStatusBadge({ status, className }: { status: ReviewTaskStatus; className?: string }) {
  const { t } = useTranslation("common");
  return (
    <Badge variant={reviewTaskStatusVariant[status] ?? "outline"} className={className} data-status={status}>
      {t(`reviewTaskStatus.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function TenantStatusBadge({ status, className }: { status: string; className?: string }) {
  const { t } = useTranslation("common");
  const variant = (tenantStatusVariant as Record<string, BadgeVariant>)[status] ?? "outline";
  return (
    <Badge variant={variant} className={className} data-status={status}>
      {t(`tenantStatus.${status}`, { defaultValue: status })}
    </Badge>
  );
}
