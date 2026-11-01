import type { TFunction } from "i18next";

/**
 * A review task's `reason` as the pipeline writes it (ValidateHandler): the open flags' codes, comma-separated and
 * sorted ("LOW_CONFIDENCE, TOTALS_MISMATCH"), or ALWAYS_REVIEW / MANUAL_REVIEW when no flag is open.
 */
export function reviewReasonCodes(reason: string | null | undefined): string[] {
  return (reason ?? "")
    .split(",")
    .map((code) => code.trim())
    .filter((code) => code.length > 0);
}

/** Each code of a reason in the UI language: a task reason of its own, else the flag code's name, else the code. */
export function reviewReasonLabels(reason: string | null | undefined, t: TFunction): string[] {
  return reviewReasonCodes(reason).map((code) => t([`queue.reasons.${code}`, `flagCodes.${code}`], { defaultValue: code }));
}

const REVIEW_REQUESTED = "document.review_requested";

/**
 * Why the document waits for a reviewer, from its timeline (`GET /documents/{id}/events`, oldest first): the `reason`
 * of the last `document.review_requested` event, the same text the queue shows as the task's reason. Undefined without
 * a timeline or such an event.
 */
export function reviewRequestReason(events: ReadonlyArray<{ eventType: string; details?: unknown }> | undefined): string | undefined {
  const last = events ? [...events].reverse().find((event) => event.eventType === REVIEW_REQUESTED) : undefined;
  const reason = last?.details && typeof last.details === "object" ? (last.details as { reason?: unknown }).reason : undefined;
  return typeof reason === "string" && reason.trim() !== "" ? reason : undefined;
}
