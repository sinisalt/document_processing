import type { DocumentStatus } from "@docproc/app-shell";
import type { DocumentEvent } from "./queries";

/**
 * What an empty flag list means. `passed`: the validation stage ran on the data on screen and raised nothing.
 * `notValidated`: the document has not (or not since its last reprocess) reached the Validate stage, so there is
 * nothing to report yet. `stale`: corrections were saved after the last validation; the API does not re-validate them.
 */
export type ValidationState = "passed" | "notValidated" | "stale";

/** Statuses only the Validate stage (or a decision after it) can lead to. */
const VALIDATED_OR_LATER: readonly DocumentStatus[] = ["Validated", "NeedsReview", "AutoApproved", "Approved", "Rejected"];

const CORRECTED = "document.corrected";
const REPROCESSED = "document.reprocessed";

/**
 * Reads the timeline (`GET /documents/{id}/events`, oldest first) when it is at hand: the last transition into
 * Validated, and whether a correction or a reprocess came after it. Without the timeline only the status decides.
 */
export function validationState(status: DocumentStatus, events: readonly DocumentEvent[] | undefined): ValidationState {
  if (!events) return VALIDATED_OR_LATER.includes(status) ? "passed" : "notValidated";

  let lastValidated = -1;
  events.forEach((event, index) => {
    if (event.toStatus === "Validated" && event.fromStatus !== "Validated") lastValidated = index;
  });
  if (lastValidated < 0) return "notValidated";

  const after = events.slice(lastValidated + 1);
  // A reprocess resets the status to before the stage it reruns; until Validate runs again its old result is void.
  if (after.some((event) => event.eventType === REPROCESSED)) return "notValidated";
  if (after.some((event) => event.eventType === CORRECTED)) return "stale";
  return "passed";
}
