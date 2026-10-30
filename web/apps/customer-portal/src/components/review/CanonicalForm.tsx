import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { Label } from "@docproc/ui";
import { ConfidenceBadge, FieldStatusBadge, SourceBadge, parsePath } from "@docproc/app-shell";
import { FieldInput } from "./FieldInput";
import { currencyOf, fieldDomId, fieldOf, headerSections, type CanonicalInvoice, type FieldSpec } from "@/lib/canonical";

export interface CanonicalFormProps {
  canonical: CanonicalInvoice | null;
  disabled: boolean;
  threshold?: number;
  onHighlight: (fieldPath: string | null) => void;
}

/** Paths whose labels are a group under `fields`; the path alone (a flag on `supplier`) is labelled from `fieldBlocks`. */
const blockRoots = new Set(["supplier", "customer", "paymentTerms", "servicePeriod", "lines", "taxBreakdown"]);

/**
 * The full i18n key of a canonical path's label: party and line paths share labels across their roots
 * (`supplier.vatId` and `customer.vatId` are both `fields.party.vatId`), list items get their 1-based index, and a bare
 * block path (`supplier`, `lines[2]`) reads `fieldBlocks.*` so it never resolves to a nested object.
 */
export function fieldLabelKey(path: string): { key: string; index?: number } {
  const segments = parsePath(path);
  const first = String(segments[0] ?? "");
  const named = segments.filter((segment) => typeof segment === "string");
  const firstIndex = segments.find((segment): segment is number => typeof segment === "number");
  if (named.length <= 1) {
    const index = firstIndex !== undefined ? firstIndex + 1 : undefined;
    return { key: blockRoots.has(first) ? `fieldBlocks.${first}` : `fields.${first}`, index };
  }
  if (first === "supplier" || first === "customer") return { key: `fields.party.${segments.slice(1).join(".")}` };
  if (first === "lines") return { key: `fields.line.${segments.slice(2).join(".")}`, index: firstIndex !== undefined ? firstIndex + 1 : undefined };
  return { key: `fields.${named.join(".")}` };
}

export function useFieldLabel() {
  const { t } = useTranslation();
  return useCallback(
    (spec: FieldSpec | string, withLineIndex = false) => {
      const path = typeof spec === "string" ? spec : spec.path;
      const { key, index } = fieldLabelKey(path);
      const label = t(key, { defaultValue: path });
      if (key.startsWith("fields.line.")) return index && withLineIndex ? t("fieldBlocks.lineField", { line: index, field: label }) : label;
      return index ? `${label} ${index}` : label;
    },
    [t],
  );
}

/** The header part of the canonical form: every `Invoice` field but the lines, grouped by section. */
export function CanonicalForm({ canonical, disabled, threshold, onHighlight }: CanonicalFormProps) {
  const { t } = useTranslation();
  const label = useFieldLabel();
  const sections = headerSections(canonical);
  return (
    <div className="flex flex-col gap-6">
      {sections.map((section) => {
        // A block field (supplier, customer, paymentTerms) carries one provenance for all its properties: badge it once.
        const blockPath = section.fields.length > 0 && section.fields.every((spec) => spec.fieldPath === section.fields[0]!.fieldPath && spec.path !== spec.fieldPath) ? section.fields[0]!.fieldPath : null;
        const block = blockPath ? fieldOf(canonical, { path: blockPath, fieldPath: blockPath, kind: "string" }) : undefined;
        return (
        <fieldset key={section.key} className="min-w-0" data-section={section.key}>
          <legend className="mb-2 flex flex-wrap items-center gap-2 text-sm font-semibold">
            {t(`review.sections.${section.key}`)}
            {block && (
              <span className="flex flex-wrap items-center gap-1 font-normal" data-path={blockPath}>
                <FieldStatusBadge status={block.status} />
                <ConfidenceBadge confidence={block.confidence} threshold={threshold} />
                <SourceBadge source={block.source} />
              </span>
            )}
          </legend>
          <div className="grid grid-cols-1 gap-x-4 gap-y-3 @lg:grid-cols-2">
            {section.fields.map((spec) => {
              const field = fieldOf(canonical, spec);
              const wide = spec.kind === "text";
              return (
                <div key={spec.path} className={wide ? "@lg:col-span-2" : undefined}>
                  <Label htmlFor={fieldDomId(spec.path)} className="mb-1 text-xs text-muted-foreground">
                    {label(spec)}
                  </Label>
                  <FieldInput
                    spec={spec}
                    field={field}
                    currency={spec.kind === "money" ? currencyOf(canonical, spec) : undefined}
                    disabled={disabled}
                    threshold={threshold}
                    onHighlight={onHighlight}
                    showBadges={spec.path === spec.fieldPath}
                  />
                </div>
              );
            })}
          </div>
        </fieldset>
        );
      })}
    </div>
  );
}
