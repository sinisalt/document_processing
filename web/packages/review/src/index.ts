// @docproc/review: the generated review form and the document type editor shared by both apps (docs/11 §12.2, §19).

export * from "./lib/formSpec";
export { legacyFormSpec, legacyInvoiceFormSpec } from "./lib/legacySpec";
export * from "./lib/values";
export * from "./lib/corrections";
export * from "./lib/lineArithmetic";
export * from "./lib/mapMode";
export * from "./lib/tableTool";
export { REVIEW_NS, fieldLabelKey, groupLabel, humanize, labelOf, localized, optionLabel, useFieldLabel } from "./lib/labels";
export { FieldInput, type FieldInputProps, type HighlightHandler, type HighlightReason } from "./components/FieldInput";
export { RepeatingGroupGrid, ArithmeticIndicator, PAGED_ABOVE, PAGE_SIZE, type RepeatingGroupGridProps } from "./components/RepeatingGroupGrid";
export { DefinitionForm, MOVE_MIME, type DefinitionFormProps } from "./components/DefinitionForm";
export { MapPreview, type MapPreviewProps } from "./components/MapPreview";
export { TablePageStrip, type TablePageStripProps } from "./components/TablePageStrip";
export { TableToolPanel, type TableToolPanelProps } from "./components/TableToolPanel";
export { reviewResources } from "./i18n";

// The document type editor (docs/11 §19, Settings > Document types).
export { DocumentTypesPanel, useTenantFeatures, type DocumentTypesPanelProps } from "./types-editor/DocumentTypesPanel";
export { DocumentTypeEditor, PublishReportView, type DocumentTypeEditorProps } from "./types-editor/DocumentTypeEditor";
export * from "./types-editor/api";
export * from "./types-editor/model";

// Configurable steps (docs/11 §10, P5): parameter forms from the kinds' schemas, the resolved pipeline, overrides.
export { ParamsForm, type ParamsFormProps } from "./pipeline/ParamsForm";
export * from "./pipeline/paramsSchema";
export * from "./pipeline/overrides";
export * from "./pipeline/api";
export { PipelineSteps, paramsText } from "./pipeline/PipelineSteps";
export * from "./types-editor/rulesModel";
export * from "./types-editor/identRules";
export { IdentificationTab, TestSummary, useTenantRules } from "./types-editor/IdentificationTab";
