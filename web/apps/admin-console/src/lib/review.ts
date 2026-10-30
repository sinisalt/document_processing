import type { components } from "@docproc/api-client";

type ReviewTask = components["schemas"]["ReviewTaskDto"];

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
