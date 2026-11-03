import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { parsePath } from "@docproc/app-shell";
import { resolvePath, uneceUnits, type FieldSpec, type FormLayout, type FormSpecField, type Locales } from "./formSpec";

/** The i18next namespace of this package (`src/i18n`); the apps register it with `reviewResources`. */
export const REVIEW_NS = "review";

/** A definition label in the UI language: the language, else English, else the first locale it has. */
export function localized(label: Locales | null | undefined, language: string): string | undefined {
  if (!label) return undefined;
  const short = language.split("-")[0] ?? language;
  return label[short] ?? label[language] ?? label.en ?? Object.values(label)[0];
}

/** Paths whose labels are a group under `fields`; the path alone (a flag on `supplier`) is labelled from `fieldBlocks`. */
const blockRoots = new Set(["supplier", "customer", "paymentTerms", "servicePeriod", "lines", "taxBreakdown"]);

/**
 * The i18n key of a canonical path's label in the built-in invoice form: party and line paths share labels across
 * their roots (`supplier.vatId` and `customer.vatId` are both `fields.party.vatId`), list items get their 1-based
 * index, and a bare block path (`supplier`, `lines[2]`) reads `fieldBlocks.*` so it never resolves to a nested object.
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

/** `supplierCustomerRef` → "Supplier customer ref": the last resort for a field nobody labelled. */
export function humanize(key: string): string {
  const words = key.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").trim().toLowerCase();
  return words ? words[0]!.toUpperCase() + words.slice(1) : key;
}

/** The label of a unit code: the unit's name in the UI language with the UN/ECE code, or the name for a version 1 tree. */
export function optionLabel(t: TFunction, spec: Pick<FieldSpec, "optionLabels" | "definitionKind">, code: string, language: string): string {
  const own = localized(spec.optionLabels?.[code], language);
  if (own) return own;
  const unitName = uneceUnits[code];
  if (unitName) return t("unit", { ns: REVIEW_NS, name: t(`enums.${unitName}`, { ns: REVIEW_NS, defaultValue: unitName }), code });
  return t(`enums.${code}`, { ns: REVIEW_NS, defaultValue: code });
}

function legacyLabel(t: TFunction, path: string, withLineIndex: boolean): string | null {
  let key: string;
  let index: number | undefined;
  try {
    ({ key, index } = fieldLabelKey(path));
  } catch {
    return null;
  }
  if (!t(key, { ns: REVIEW_NS, defaultValue: "" })) return null;
  const label = t(key, { ns: REVIEW_NS });
  if (key.startsWith("fields.line.")) return index && withLineIndex ? t("fieldBlocks.lineField", { ns: REVIEW_NS, line: index, field: label }) : label;
  return index ? `${label} ${index}` : label;
}

function memberLabel(t: TFunction, of: string, member: string, parent: string): string {
  const key = `members.${of}.${member}`;
  return t(key, { ns: REVIEW_NS, field: parent, defaultValue: "" }) || humanize(member.split(".").pop() ?? member);
}

/**
 * The label of a canonical path in the UI language (docs/11 §12.2 `useFieldLabel`): the definition's label of the
 * field, else the built-in i18n label (`fields.*`, the invoice form's), else the generic label of a composite's member
 * (`members.party.vatId`), else the key in words. With `withLineIndex`, a row field says which row
 * ("Line 3 · Qty"), and a list entry its 1-based number.
 */
export function labelOf(t: TFunction, language: string, layout: Pick<FormLayout, "fields"> | null | undefined, path: string, withLineIndex = false): string {
  const fields = layout?.fields;
  const resolved = fields && fields.size > 0 ? resolvePath(fields, path) : null;
  const field = resolved?.field ?? null;
  const own = field && !resolved?.member ? localized(field.label, language) : undefined;

  if (!own) {
    // The built-in labels win over a member label: they are what the invoice form always said.
    const legacy = legacyLabel(t, path, withLineIndex);
    if (legacy) return legacy;
  }

  let label: string;
  if (own) {
    label = own;
  } else if (field?.kind === "bag" && resolved?.member) {
    // A supplier-specific attribute is named by its key, as the supplier's data has it.
    label = resolved.member;
  } else if (field && resolved?.member) {
    const parent = localized(field.label, language) ?? humanize(field.key);
    label = memberLabel(t, field.kind, resolved.member, parent);
  } else if (field) {
    label = humanize(field.key);
  } else {
    const last = path.match(/[A-Za-z_$][\w$]*/g)?.pop() ?? path;
    label = humanize(last);
  }

  if (resolved?.entry !== null && resolved?.entry !== undefined) label = `${label} ${resolved.entry + 1}`;
  const row = resolved?.row;
  if (row) {
    // A document's lines read "Line 3 · Qty" as they always did; any other group "Articles 3 · Price".
    const lines = row.group.key === "lines";
    const group = lines ? t("fieldBlocks.lines", { ns: REVIEW_NS }) : groupLabel(t, language, row.group);
    if (field === row.group) return t("grid.row", { ns: REVIEW_NS, group, row: row.index + 1 });
    if (withLineIndex) return lines ? t("fieldBlocks.lineField", { ns: REVIEW_NS, line: row.index + 1, field: label }) : t("grid.rowField", { ns: REVIEW_NS, group, row: row.index + 1, field: label });
  }
  return label;
}

/** A repeating group's own label ("Lines", "Articles"). */
export function groupLabel(t: TFunction, language: string, group: FormSpecField): string {
  return localized(group.label, language) ?? (t(`sections.${group.key}`, { ns: REVIEW_NS, defaultValue: "" }) || humanize(group.key));
}

/** `labelOf` bound to the UI language and a layout. */
export function useFieldLabel(layout?: Pick<FormLayout, "fields"> | null) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  return useCallback((spec: FieldSpec | string, withLineIndex = false) => labelOf(t, i18n.language, layout, typeof spec === "string" ? spec : spec.path, withLineIndex), [t, i18n.language, layout]);
}
