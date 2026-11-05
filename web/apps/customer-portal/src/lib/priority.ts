/**
 * The review priority of docs/11 §11 (`review.priority`, `dueBy`, P5): a daily specials list is urgent until the end of
 * its day in the tenant's time zone (`ReviewTask.SlaDueAt`). The inbox and the queue show how close the due time is; the
 * API puts the urgent ones first across every page (`GET /documents?sort=reviewDue`, §19 item 7). Pure, so it is tested
 * without rendering.
 */
export type ReviewPriority = "Normal" | "High" | "Urgent" | null | undefined;

export type DueState = "overdue" | "soon" | "later";

/** Within this many hours of its due time a review counts as due soon. */
export const SOON_HOURS = 4;

/** How close a due time is: past it, within `SOON_HOURS`, or later; null without one. */
export function dueState(dueAt: string | null | undefined, now: Date = new Date()): DueState | null {
  if (!dueAt) return null;
  const due = Date.parse(dueAt);
  if (Number.isNaN(due)) return null;
  const left = due - now.getTime();
  if (left < 0) return "overdue";
  return left <= SOON_HOURS * 3_600_000 ? "soon" : "later";
}

/** The inbox's order for the API: `reviewDue` ("Urgent first") or the default (newest first). */
export function documentSortOf(sort: string | undefined): "reviewDue" | undefined {
  return sort === "reviewDue" ? "reviewDue" : undefined;
}
