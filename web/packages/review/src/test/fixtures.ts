import { specsFromDefinition, type FieldSpec, type FormLayout } from "../lib/formSpec";
import { legacyInvoiceFormSpec } from "../lib/legacySpec";

/** The built-in invoice form over a version 1 tree, as the review screen lays it out for a tenant without definitions. */
export function legacyLayout(canonical: unknown): FormLayout {
  return specsFromDefinition(legacyInvoiceFormSpec, canonical, { schemaVersion: 1 });
}

/** Every editable spec of the built-in invoice form (what `allSpecs` of the earlier `lib/canonical.ts` returned). */
export function allSpecs(canonical: unknown): FieldSpec[] {
  return legacyLayout(canonical).specs;
}

export const extracted = <T,>(value: T, confidence = 1) => ({ value, status: "Extracted" as const, confidence, source: { method: "StructuredXml" as const } });
export const missing = () => ({ status: "Missing" as const, history: [] });
export const money = (amount: number, currency = "EUR") => ({ amount, currency });
