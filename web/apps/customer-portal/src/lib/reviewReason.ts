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
