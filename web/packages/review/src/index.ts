// @docproc/review: the generated review form and the document type editor shared by both apps (docs/11 §12.2, §19).

export * from "./lib/formSpec";
export { legacyFormSpec, legacyInvoiceFormSpec } from "./lib/legacySpec";
export * from "./lib/values";
export * from "./lib/corrections";
export * from "./lib/lineArithmetic";
export { REVIEW_NS, fieldLabelKey, groupLabel, humanize, labelOf, localized, optionLabel, useFieldLabel } from "./lib/labels";
export { FieldInput, type FieldInputProps, type HighlightHandler, type HighlightReason } from "./components/FieldInput";
export { RepeatingGroupGrid, ArithmeticIndicator, PAGED_ABOVE, PAGE_SIZE, type RepeatingGroupGridProps } from "./components/RepeatingGroupGrid";
export { DefinitionForm, type DefinitionFormProps } from "./components/DefinitionForm";
export { reviewResources } from "./i18n";
