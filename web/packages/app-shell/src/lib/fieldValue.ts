import type { ComponentProps } from "react";
import type { Badge } from "@docproc/ui";

export type BadgeVariant = NonNullable<ComponentProps<typeof Badge>["variant"]>;

/**
 * The canonical model's `FieldValue<T>` as the API stores it verbatim in `DocumentDetail.canonical`
 * (`src/DocProc.Domain/Fields/*.cs`, camel-cased by System.Text.Json; enums as names).
 */
export type FieldStatus = "Extracted" | "Inferred" | "Missing" | "Unreadable" | "Conflict" | "Corrected";
export type SourceMethod = "StructuredXml" | "Profile" | "Llm" | "Ocr" | "Human";

/** Page coordinates normalised to 1000 × 1000 units (docs/04). */
export interface BoundingBox {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

export interface FieldSource {
  method: SourceMethod;
  page?: number | null;
  bBox?: BoundingBox | null;
  rawText?: string | null;
}

export interface FieldHistoryEntry<T = unknown> {
  value: T | null;
  status: FieldStatus;
  source?: FieldSource | null;
  confidence?: number | null;
  at: string;
  by: string;
}

export interface FieldValue<T = unknown> {
  value: T | null;
  status: FieldStatus;
  /** Calibrated 0..1, or null when the source gives none. */
  confidence?: number | null;
  source?: FieldSource | null;
  history?: FieldHistoryEntry<T>[];
}

export const fieldStatuses: readonly FieldStatus[] = ["Extracted", "Inferred", "Missing", "Unreadable", "Conflict", "Corrected"];
export const sourceMethods: readonly SourceMethod[] = ["StructuredXml", "Profile", "Llm", "Ocr", "Human"];

export function isFieldValue(value: unknown): value is FieldValue {
  if (typeof value !== "object" || value === null) return false;
  const v = value as Record<string, unknown>;
  return "value" in v && typeof v.status === "string" && (fieldStatuses as readonly string[]).includes(v.status);
}

/** Mirrors `FieldValue.HasValue`: a value is present and the status is not Missing or Unreadable. */
export function hasValue(field: FieldValue | null | undefined): boolean {
  return !!field && field.value !== null && field.value !== undefined && field.status !== "Missing" && field.status !== "Unreadable";
}

/** The review policy's `autoApproveConfidenceThreshold` default; screens pass the tenant's own. */
export const DEFAULT_CONFIDENCE_THRESHOLD = 0.8;

/**
 * A field the reviewer should look at: a known confidence below the threshold, or a status that means the pipeline
 * itself was not sure (Conflict, Unreadable). Missing fields are not "low confidence" (nothing was read); a human
 * correction never is.
 */
export function isLowConfidence(field: FieldValue | null | undefined, threshold = DEFAULT_CONFIDENCE_THRESHOLD): boolean {
  if (!field) return false;
  if (field.status === "Corrected" || field.status === "Missing") return false;
  if (field.status === "Conflict" || field.status === "Unreadable") return true;
  return typeof field.confidence === "number" && field.confidence < threshold;
}

export const fieldStatusVariant: Readonly<Record<FieldStatus, BadgeVariant>> = {
  Extracted: "secondary",
  Inferred: "outline",
  Missing: "outline",
  Unreadable: "destructive",
  Conflict: "destructive",
  Corrected: "default",
};

export const sourceMethodVariant: Readonly<Record<SourceMethod, BadgeVariant>> = {
  StructuredXml: "secondary",
  Profile: "secondary",
  Llm: "outline",
  Ocr: "outline",
  Human: "default",
};

/**
 * Three bands: at or above 0.95 is plainly fine, between threshold and 0.95 is "look", below the threshold is "check":
 * amber, because a low confidence raises a warning flag (LOW_CONFIDENCE), not a blocking one, and only a failure or a
 * blocking problem reads red.
 */
export function confidenceVariant(confidence: number | null | undefined, threshold = DEFAULT_CONFIDENCE_THRESHOLD): BadgeVariant {
  if (typeof confidence !== "number") return "outline";
  if (confidence < threshold) return "warning";
  if (confidence < 0.95) return "outline";
  return "secondary";
}
