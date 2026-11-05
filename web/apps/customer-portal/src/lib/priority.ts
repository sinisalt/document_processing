/**
 * The review priority of docs/11 §11 (`review.priority`, `dueBy`, P5): a daily specials list is urgent until the end of
 * its day in the tenant's time zone (`ReviewTask.SlaDueAt`). The inbox and the queue show how close the due time is and
 * can put the urgent ones first (`?sort=reviewDue`, §19 item 7). Pure, so the order is tested without rendering.
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

const rank: Record<string, number> = { Urgent: 0, High: 1, Normal: 2 };

/**
 * The order of `?sort=reviewDue`: urgent first, then high, then the rest; within a priority the earliest due time
 * first, rows without one after those with one, and otherwise the order the list came in (newest first).
 */
export function sortByReviewDue<T>(rows: readonly T[], priorityOf: (row: T) => ReviewPriority, dueOf: (row: T) => string | null | undefined): T[] {
  return rows
    .map((row, index) => ({ row, index }))
    .sort((a, b) => {
      const pa = rank[priorityOf(a.row) ?? "Normal"] ?? 2;
      const pb = rank[priorityOf(b.row) ?? "Normal"] ?? 2;
      if (pa !== pb) return pa - pb;
      const da = dueOf(a.row) ? Date.parse(dueOf(a.row)!) : Number.POSITIVE_INFINITY;
      const db = dueOf(b.row) ? Date.parse(dueOf(b.row)!) : Number.POSITIVE_INFINITY;
      if (da !== db) return da - db;
      return a.index - b.index;
    })
    .map((entry) => entry.row);
}
