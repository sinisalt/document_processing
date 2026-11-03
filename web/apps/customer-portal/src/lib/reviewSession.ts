import { useEffect, useState } from "react";
import type { TableToolState } from "@docproc/review";

/**
 * What the review screen keeps for a document while the reviewer stays in this tab: the screen is remounted for every
 * server version of the document (docs/11 §12.1, the `id:updatedAt` key), and a save in map mode or the table tool
 * must not switch the tool off, lose the page, or forget the table mapping drawn so far.
 */
export interface ReviewSession {
  /** The reviewer's choice; null (the default) follows the document: on while none of its fields holds a value. */
  mapMode?: boolean | null;
  tableMode?: boolean;
  page?: number;
  tab?: string;
  /** The field map mode fills (its form path). */
  mapTarget?: string | null;
  table?: TableToolState | null;
}

const sessions = new Map<string, ReviewSession>();

/** The most documents whose session is kept; the oldest goes first. */
const MAX_SESSIONS = 50;

export function readReviewSession(documentId: string): ReviewSession {
  return sessions.get(documentId) ?? {};
}

export function writeReviewSession(documentId: string, patch: ReviewSession): void {
  const next = { ...sessions.get(documentId), ...patch };
  sessions.delete(documentId);
  sessions.set(documentId, next);
  while (sessions.size > MAX_SESSIONS) {
    const oldest = sessions.keys().next().value;
    if (oldest === undefined) break;
    sessions.delete(oldest);
  }
}

export function clearReviewSessions(): void {
  sessions.clear();
}

/** A state of the review screen that outlives the screen's remounts for the same document (null included). */
export function useSessionState<K extends keyof ReviewSession>(documentId: string, key: K, initial: Exclude<ReviewSession[K], undefined> | (() => Exclude<ReviewSession[K], undefined>)) {
  type Value = Exclude<ReviewSession[K], undefined>;
  const [value, setValue] = useState<Value>(() => {
    const stored = readReviewSession(documentId);
    if (key in stored && stored[key] !== undefined) return stored[key] as Value;
    return typeof initial === "function" ? (initial as () => Value)() : initial;
  });
  useEffect(() => {
    writeReviewSession(documentId, { [key]: value } as ReviewSession);
  }, [documentId, key, value]);
  return [value, setValue] as const;
}
