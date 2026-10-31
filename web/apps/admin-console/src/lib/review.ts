import type { components } from "@docproc/api-client";

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
