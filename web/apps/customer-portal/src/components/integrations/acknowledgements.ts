/**
 * A destination's acknowledgements (docs/11 §13.8, as built in P5 part C): `{callback?: {auth, mapping?}, statusPoll?:
 * {urlTemplate, intervalMinutes, mapping?, maxPolls?}}` as `PUT /destinations/{id}/acknowledgements` takes them. The
 * form edits a flat draft; these turn the stored settings into it and back. Pure, so the mapping is tested.
 */
export type CallbackAuth = "hmacSha256" | "bearer";

export interface AcknowledgementDraft {
  callback: boolean;
  auth: CallbackAuth;
  callbackMapping: string;
  poll: boolean;
  urlTemplate: string;
  intervalMinutes: string;
  maxPolls: string;
  pollMapping: string;
}

const text = (value: unknown): string => (typeof value === "string" ? value : typeof value === "number" ? String(value) : "");

export function acknowledgementDraft(stored: unknown): AcknowledgementDraft {
  const root = stored && typeof stored === "object" ? (stored as Record<string, unknown>) : {};
  const callback = root.callback && typeof root.callback === "object" ? (root.callback as Record<string, unknown>) : null;
  const poll = root.statusPoll && typeof root.statusPoll === "object" ? (root.statusPoll as Record<string, unknown>) : null;
  return {
    callback: callback !== null,
    auth: callback?.auth === "bearer" ? "bearer" : "hmacSha256",
    callbackMapping: text(callback?.mapping),
    poll: poll !== null,
    urlTemplate: text(poll?.urlTemplate),
    intervalMinutes: poll ? text(poll.intervalMinutes) || "15" : "15",
    maxPolls: text(poll?.maxPolls),
    pollMapping: text(poll?.mapping),
  };
}

/** The body of `PUT /destinations/{id}/acknowledgements`: an empty object turns both off. */
export function acknowledgementBody(draft: AcknowledgementDraft): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  if (draft.callback) body.callback = { auth: draft.auth, ...(draft.callbackMapping.trim() ? { mapping: draft.callbackMapping } : {}) };
  if (draft.poll) {
    const interval = Number.parseInt(draft.intervalMinutes, 10);
    const maxPolls = Number.parseInt(draft.maxPolls, 10);
    body.statusPoll = {
      urlTemplate: draft.urlTemplate.trim(),
      intervalMinutes: Number.isFinite(interval) ? interval : 15,
      ...(Number.isFinite(maxPolls) ? { maxPolls } : {}),
      ...(draft.pollMapping.trim() ? { mapping: draft.pollMapping } : {}),
    };
  }
  return body;
}

/** Where the receiver posts its callbacks for this destination. */
export function callbackUrl(origin: string, destinationId: string): string {
  return `${origin.replace(/\/$/, "")}/api/v1/integrations/callbacks/${destinationId}`;
}
