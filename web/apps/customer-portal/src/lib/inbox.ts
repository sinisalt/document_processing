import { inFlightStatuses, type DocumentStatus } from "@docproc/app-shell";

export const inboxTabs = ["needsReview", "processing", "approved", "rejected", "failed", "all"] as const;
export type InboxTab = (typeof inboxTabs)[number];

/** The `status` filter of `GET /documents` (comma list) each inbox tab stands for; `all` sends none. */
export const tabStatuses: Readonly<Record<InboxTab, readonly DocumentStatus[]>> = {
  needsReview: ["NeedsReview"],
  processing: [...inFlightStatuses, "Validated"],
  approved: ["Approved", "AutoApproved"],
  rejected: ["Rejected"],
  failed: ["Failed"],
  all: [],
};

export function statusFilterOf(tab: InboxTab): string | undefined {
  const statuses = tabStatuses[tab];
  return statuses.length > 0 ? statuses.join(",") : undefined;
}

export function isInboxTab(value: unknown): value is InboxTab {
  return typeof value === "string" && (inboxTabs as readonly string[]).includes(value);
}

/** The dashboard's status groups: the tabs that count something. */
export const countedTabs: readonly InboxTab[] = ["needsReview", "processing", "approved", "rejected", "failed"];
