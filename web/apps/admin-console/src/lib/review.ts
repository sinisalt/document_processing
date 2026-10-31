import type { components } from "@docproc/api-client";
import type { TFunction } from "i18next";

type ReviewTask = components["schemas"]["ReviewTaskDto"];

/**
 * The flag codes a review task's `reason` carries (docs/02 §5, the same list the portal translates). The queue shows
 * `flagCodes.<code>` and falls back to the code itself for one it does not know.
 */
export const flagCodes = [
  "UNREADABLE_PAGES",
  "TYPE_UNKNOWN",
  "SUPPLIER_UNMATCHED",
  "CUSTOMER_MISMATCH",
  "MISSING_REQUIRED",
  "TOTALS_MISMATCH",
  "TAX_MISMATCH",
  "LINE_ARITHMETIC",
  "LOW_CONFIDENCE",
  "DUPLICATE_SUSPECTED",
  "PROFILE_FALLBACK",
  "LLM_PROVIDER_UNAVAILABLE",
  "MULTI_DOCUMENT_SPLIT",
  "CURRENCY_INFERRED",
  "PO_UNMATCHED",
  "DATE_IMPLAUSIBLE",
  "INVALID_VALUE",
  "UNSUPPORTED_TYPE",
] as const;

/** What the queue offers on a task, by the API's rules (ReviewTaskEndpoints). */
export type ReviewTaskAction = "claim" | "release" | "releaseOther" | null;

/**
 * - closed (Done / Skipped): nothing; the API answers 409 "Task closed".
 * - unassigned: Claim.
 * - assigned to the caller (`me`, the API's `ActorName`): Release.
 * - assigned to someone else: Release only with the TenantAdmin policy (SuperAdmin, TenantAdmin), behind a confirm;
 *   anyone else gets 409 "Task held by another reviewer", so it is not offered.
 */
export function reviewTaskAction(task: Pick<ReviewTask, "status" | "assignedTo">, me: string | null, canReleaseOthers: boolean): ReviewTaskAction {
  if (task.status === "Done" || task.status === "Skipped") return null;
  if (!task.assignedTo) return "claim";
  if (me !== null && task.assignedTo === me) return "release";
  return canReleaseOthers ? "releaseOther" : null;
}

/**
 * A task's `reason` as the pipeline writes it (ValidateHandler): the open flags' codes, comma-separated and sorted, or
 * ALWAYS_REVIEW / MANUAL_REVIEW when no flag is open. Each code reads as a task reason of its own (`review.reasons`),
 * else as the flag code's name, else as the code.
 */
export function reviewReasonLabels(reason: string | null | undefined, t: TFunction): { code: string; label: string }[] {
  return (reason ?? "")
    .split(",")
    .map((code) => code.trim())
    .filter((code) => code.length > 0)
    .map((code) => ({ code, label: t([`review.reasons.${code}`, `flagCodes.${code}`], { defaultValue: code }) }));
}
