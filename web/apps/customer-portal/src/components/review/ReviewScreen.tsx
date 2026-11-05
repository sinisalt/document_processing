import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useBlocker, useNavigate } from "@tanstack/react-router";
import { FormProvider, useForm } from "react-hook-form";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft, Loader2, Save } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Button, cn, ResizableHandle, ResizablePanel, ResizablePanelGroup, Tabs, TabsContent, TabsList, TabsTrigger, useIsMobile } from "@docproc/ui";
import { ApiError, ConfirmDialog, EmptyState, PageHeader, StatusBadge, formatDate, formatMoney, isCorrectable, isInFlight, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import {
  DefinitionForm,
  MapPreview,
  TableToolPanel,
  defaultTablePath,
  diffCorrections,
  fieldDomId,
  formName,
  isFormSpec,
  legacyFormSpec,
  localized,
  nextEmptyRequired,
  rawValueOf,
  specsFromDefinition,
  toFormValues,
  useFieldLabel,
  type FieldCorrection,
  type FormSpec,
  type HighlightReason,
  type MappedSources,
  useTenantFeatures,
} from "@docproc/review";
import { DeliveriesTab } from "./DeliveriesTab";
import { FlagsList, sortFlags } from "./FlagsList";
import { IdentificationCard } from "./IdentificationCard";
import { LayoutSavedDialog } from "./LayoutSavedDialog";
import { NewLayoutPanel, defaultLayoutChoice } from "./NewLayoutPanel";
import { IntegrationIssuePanel, RevisionPanel, openFlag } from "./RevisionPanel";
import { TenantRuleAction } from "./TenantRuleAction";
import { useIdentify } from "./useIdentify";
import { MapDocumentPanel } from "./MapDocumentPanel";
import { OriginalTab } from "./OriginalTab";
import { PageViewer, type PageViewerTools } from "./PageViewer";
import { ReviewActions } from "./ReviewActions";
import { Timeline } from "./Timeline";
import { useMapMode } from "./useMapMode";
import { useTableTool } from "./useTableTool";
import { collectProvenanceBoxes } from "@/lib/overlay";
import { useSupplierLayouts } from "@/lib/layoutQueries";
import { EMPTY_DRAFT, appliedEvidence, appliedLayout, evidenceBoxes, learnLayoutRequest, openLayoutFlag } from "@/lib/layouts";
import { useDocumentEvents, type DocumentDetail, type DocumentFlag } from "@/lib/queries";
import { useSessionState } from "@/lib/reviewSession";
import { reviewReasonLabels, reviewRequestReason } from "@/lib/reviewReason";
import { validationState } from "@/lib/validation";

/** The decisions an approval or rejection leaves, which fan out to the tenant's bindings (docs/11 §13.5). */
const decidedStatuses: ReadonlySet<DocumentDetail["status"]> = new Set(["Approved", "AutoApproved", "Rejected"]);

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName) || target.getAttribute("role") === "combobox";
}

/** A dialog, or a Radix popover (Select listbox, dropdown menu), that a key press belongs to rather than to the screen. */
const OVERLAY_SELECTOR = '[role="dialog"], [role="alertdialog"], [role="listbox"], [role="menu"], [data-radix-popper-content-wrapper]';

const blockAlways = () => true;

export interface FocusFieldOptions {
  /**
   * Move the keyboard focus into the field's input (default). j/k pass false: the input would swallow the next j/k
   * as text, so they only select the flag, scroll to its field and highlight it.
   */
  focus?: boolean;
}

export interface ReviewScreenProps {
  document: DocumentDetail;
  canReview: boolean;
  threshold: number;
  locale: string;
  /** A tenant admin (the supplier master data of "create supplier from this document"). */
  canManageSuppliers?: boolean;
  /** An integration admin (redelivery in the Deliveries tab). */
  canRedeliver?: boolean;
  /** A ConfigAdmin ("use for all suppliers without own layouts", the tenant fallback rules of docs/11 §8.5). */
  canConfigure?: boolean;
}

/** Remounted (by key) whenever the server's version of the document changes, so the form always starts from it. */
export function ReviewScreen({ document, canReview, threshold, locale, canManageSuppliers = false, canRedeliver = false, canConfigure = false }: ReviewScreenProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { client } = useApi();
  const queryClient = useQueryClient();
  // Below md (768 px) the viewer sits above the form instead of beside it: side by side, neither fits a phone.
  const stacked = useIsMobile();

  // The form comes from the type version the API resolved (docs/11 §12.2); an invoice or credit note of a tenant without
  // definitions gets the built-in invoice form. Any other type without a definition has no form.
  const formSpec = useMemo<FormSpec | null>(() => {
    const served = document.definition?.formSpec;
    return isFormSpec(served) ? served : legacyFormSpec(document.type);
  }, [document.definition, document.type]);
  const hasCanonical = !!formSpec && !!document.canonical;
  const canonical = useMemo(() => (hasCanonical ? (document.canonical as unknown) : null), [document.canonical, hasCanonical]);
  const schemaVersion = document.canonicalSchemaVersion === null ? null : Number(document.canonicalSchemaVersion);
  const invoiceFamily = document.type === "Invoice" || document.type === "CreditNote";
  const layout = useMemo(() => (formSpec && canonical ? specsFromDefinition(formSpec, canonical, { schemaVersion, invoiceFamily }) : null), [formSpec, canonical, schemaVersion, invoiceFamily]);
  const specs = useMemo(() => layout?.specs ?? [], [layout]);
  const fieldLabel = useFieldLabel(layout);
  const initial = useMemo(() => toFormValues(canonical, specs, locale), [canonical, specs, locale]);
  const boxes = useMemo(() => collectProvenanceBoxes(canonical), [canonical]);
  const flags = useMemo(() => sortFlags(document.flags), [document.flags]);
  const flaggedPaths = useMemo(() => flags.map((f) => f.field).filter((path): path is string => !!path), [flags]);
  /** A flag's field has a box when the field, or the block it belongs to (`supplier` for `supplier.vatId`), has one. */
  const locatedOnPage = useCallback((path: string) => boxes.some((box) => box.path === path || path.startsWith(`${box.path}.`) || path.startsWith(`${box.path}[`)), [boxes]);

  // An empty flag list only means "every rule passed" when Validate ran on what is on screen; the timeline tells. The
  // screen remounts for every server version of the document, so it reads the timeline afresh each time: a cached one
  // can predate the Validate run that produced this version.
  const events = useDocumentEvents(document.id, flags.length === 0, { refetchOnMount: "always" });
  const validation = validationState(document.status, events.data);
  const reviewReason = reviewRequestReason(events.data);

  const editable = canReview && isCorrectable(document.status) && hasCanonical;
  const settled = !isCorrectable(document.status) && !isInFlight(document.status);

  const form = useForm<Record<string, unknown>>({ defaultValues: initial, mode: "onBlur" });
  // Values taken from the page in map mode (docs/11 §12.3): saved with the place they were read from.
  const [mapped, setMapped] = useState<MappedSources>({});
  // "Unsaved" means a correction would be sent, not that an input was touched: "58,80" for 58.8 changes nothing.
  const [hasChanges, setHasChanges] = useState(false);
  useEffect(() => {
    const subscription = form.watch((values) => setHasChanges(diffCorrections(specs, initial, values).length > 0));
    return () => subscription.unsubscribe();
  }, [form, specs, initial]);

  // Leaving with unsaved corrections asks first: the back button, Esc, the sidebar, browser Back, and closing the tab.
  const leave = useBlocker({ shouldBlockFn: blockAlways, enableBeforeUnload: hasChanges, disabled: !hasChanges, withResolver: true });

  // What the pointer is over (a field in the form, a box on the page) wins while it is there; otherwise the field being
  // edited (focused, or picked from a flag or a box) stays highlighted. One shared value let a pointer leaving any
  // field clear the highlight of the focused one.
  const [hoveredPath, setHoveredPath] = useState<string | null>(null);
  const [focusedPath, setFocusedPath] = useState<string | null>(null);
  const highlightedPath = hoveredPath ?? focusedPath;
  const [page, setPage] = useSessionState(document.id, "page", 1);
  const [tab, setTab] = useSessionState(document.id, "tab", "fields");
  const [approveOpen, setApproveOpen] = useState(false);
  const [selectedFlag, setSelectedFlag] = useState(-1);

  const pageCount = Number(document.pageCount) || 0;

  // Map mode and the table tool (docs/11 §12.3, §12.4). Map mode starts on for a document whose fields hold no value
  // yet (a type just set in review); the choice, the field it fills and the table drawn survive the screen's remounts.
  // Like every review tool of P2 they need the tenant's configurableTypes (docs/11 principle 6).
  const mapAvailable = editable && pageCount > 0 && document.configurableTypes === true;
  const groupPath = useMemo(() => (layout?.structural ? defaultTablePath(formSpec) : null), [layout, formSpec]);
  const tableAvailable = mapAvailable && groupPath !== null;
  const noValues = useMemo(() => specs.length > 0 && specs.every((spec) => rawValueOf(canonical, spec) === undefined), [specs, canonical]);
  const [mapModeChoice, setMapMode] = useSessionState(document.id, "mapMode", null);
  const [tableModeChoice, setTableMode] = useSessionState(document.id, "tableMode", false);
  const mapMode = mapAvailable && (mapModeChoice ?? noValues) && !(tableAvailable && tableModeChoice);
  // Once map mode came on by itself, it stays on after the first values are saved (the screen remounts with them).
  useEffect(() => {
    if (mapModeChoice === null && mapAvailable && noValues) setMapMode(true);
  }, [mapModeChoice, mapAvailable, noValues, setMapMode]);
  const tableMode = tableAvailable && tableModeChoice;
  const [mapTarget, setMapTarget] = useSessionState(document.id, "mapTarget", null);
  const targetSpec = useMemo(() => (mapTarget ? (specs.find((s) => s.path === mapTarget) ?? null) : null), [specs, mapTarget]);
  const map = useMapMode(document.id, targetSpec, locale);
  const table = useTableTool(document, formSpec, groupPath, groupPath !== null && pageCount > 0);
  // Layouts (docs/11 §12.9, §12.10): the New layout panel for a document held with NEW_LAYOUT or LAYOUT_AMBIGUOUS, how
  // "Approve and save as layout" saves it, and Identify mode with its Identification card. Only documents of a tenant
  // with supplierLayouts carry these flags or a layout, so every other tenant sees the screen as before.
  const layoutFlag = useMemo(() => openLayoutFlag(document), [document]);
  const newLayout = layoutFlag?.code === "NEW_LAYOUT" ? layoutFlag : null;
  const identifiedLayout = useMemo(() => appliedLayout(document.appliedConfig), [document.appliedConfig]);
  const learnable = canReview && isCorrectable(document.status) && newLayout !== null && !!document.supplierId;
  // A tenant fallback rule (docs/11 §8.5, §12.10 step 5, P5): a ConfigAdmin marks identifiers on a typed document no
  // layout filled (a supplier without own layouts, or none known) and offers them for every supplier without layouts.
  const tenantFeatures = useTenantFeatures();
  const tenantRuleMarkable =
    canConfigure && canReview && tenantFeatures.data?.supplierLayouts === true && isCorrectable(document.status) && newLayout === null && !document.supplierLayoutVersionId && document.type !== "Unknown" && pageCount > 0;
  const identifyAvailable = pageCount > 0 && ((!!document.supplierId && (learnable || (identifiedLayout !== null && !!document.supplierLayoutVersionId))) || tenantRuleMarkable);
  const canMarkIdentifiers = learnable || tenantRuleMarkable;
  const supplierLayouts = useSupplierLayouts(document.supplierId, newLayout !== null);
  const [identifyChoice, setIdentifyMode] = useSessionState(document.id, "identifyMode", false);
  const identifyMode = identifyAvailable && identifyChoice;
  const [identification, setIdentification] = useSessionState(document.id, "identification", EMPTY_DRAFT);
  const [storedChoice, setLayoutChoice] = useSessionState(document.id, "layoutChoice", null);
  const layoutChoice = useMemo(
    () => storedChoice ?? (newLayout ? defaultLayoutChoice(document, newLayout, (supplierLayouts.data ?? []).map((l) => l.layoutKey)) : null),
    [storedChoice, newLayout, document, supplierLayouts.data],
  );
  const [savedLayout, setSavedLayout] = useSessionState(document.id, "savedLayout", null);
  const identify = useIdentify(document.id, document.supplierId ?? null, identification, setIdentification, document.definition?.key ?? null, !learnable && tenantRuleMarkable ? "tenant" : "supplier");
  const evidence = useMemo(() => {
    if (!identifyMode) return [];
    const tested = identify.here.data?.documents.find((d) => d.documentId === document.id);
    return evidenceBoxes(tested ? tested.evidence : identify.rule === null ? appliedEvidence(document.appliedConfig) : [], page);
  }, [identifyMode, identify.here.data, identify.rule, document.id, document.appliedConfig, page]);
  const layoutRequest = layoutChoice ? learnLayoutRequest(layoutChoice, identification) : null;

  // The Table tab: the tool while the document can be corrected, and the stored report (page strip) afterwards.
  const showTableTab = tableAvailable || (groupPath !== null && table.report !== null);
  // The Deliveries tab (docs/11 §19) for a tenant with the outputs switch (principle 6); others see the screen as before.
  const showDeliveriesTab = document.outputs === true;
  // When the decision the deliveries fan out from was taken (approvals keep their time; a rejection is the last update).
  const settledAt = showDeliveriesTab && decidedStatuses.has(document.status) ? (document.approvedAt ?? document.updatedAt) : null;

  /** Hover and focus in the form highlight the field's box; focus also turns to its page, so the box is in sight. */
  const highlightField = useCallback(
    (path: string | null, reason?: HighlightReason) => {
      if (reason !== "focus") {
        setHoveredPath(path);
        return;
      }
      setFocusedPath(path);
      if (path === null) return;
      const box = boxes.find((b) => b.path === path);
      if (box) setPage(box.page);
    },
    [boxes],
  );

  /** Show a canonical path (or the first input inside a block field): its page, its highlight, its input in view. */
  const focusField = useCallback(
    (path: string, { focus = true }: FocusFieldOptions = {}) => {
      const spec = specs.find((s) => s.path === path) ?? specs.find((s) => s.fieldPath === path) ?? specs.find((s) => s.path.startsWith(`${path}.`) || s.path.startsWith(`${path}[`));
      const box = boxes.find((b) => b.path === path) ?? (spec ? boxes.find((b) => b.path === spec.fieldPath) : undefined);
      if (box) setPage(box.page);
      setFocusedPath(spec?.fieldPath ?? path);
      setTab("fields");
      const target = spec ? window.document.getElementById(fieldDomId(spec.path)) : null;
      if (target) {
        // "nearest": only as far as needed, and not at all when the field is already in sight. "center" also scrolled
        // the window, pushing the header with Approve off the screen when a flag was picked whose field was visible.
        target.scrollIntoView({ block: "nearest", behavior: "smooth" });
        if (focus) target.focus({ preventScroll: true });
      }
    },
    [specs, boxes],
  );

  const jumpToFlag = useCallback(
    (flag: DocumentFlag) => {
      if (flag.field) focusField(flag.field);
    },
    [focusField],
  );

  /** j/k: select a flag and show its field, keeping the focus on the flag so Enter edits the field and j/k go on. */
  const selectFlag = useCallback(
    (index: number) => {
      setSelectedFlag(index);
      const flag = flags[index];
      if (!flag) return;
      if (flag.field) focusField(flag.field, { focus: false });
      window.document.querySelector<HTMLElement>(`[data-flag-index="${index}"]`)?.focus({ preventScroll: true });
    },
    [flags, focusField],
  );

  const save = useMutation({
    mutationFn: async (values: Record<string, unknown>) => {
      const corrections = diffCorrections(specs, initial, values, undefined, mapped);
      if (corrections.length === 0) return null;
      return unwrap(await client.PATCH("/api/v1/documents/{id}/fields", { params: { path: { id: document.id } }, body: { corrections } }));
    },
    onSuccess: (updated, values) => {
      if (!updated) {
        toast.info(t("review.save.nothing"));
        return;
      }
      const count = diffCorrections(specs, initial, values).length;
      queryClient.setQueryData(queryKeys.documents.detail(document.id), updated);
      void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
      toast.success(t("review.save.saved", { count }));
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) {
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          const spec = specs.find((s) => s.path === key);
          if (spec) form.setError(formName(spec.path), { type: "server", message: messages.join(" ") });
        }
      }
    },
  });

  const onSave = form.handleSubmit((values) => save.mutate(values));

  // Adding or removing a row, a list entry or an attribute (definition-driven documents, docs/11 §12.8) is sent at once,
  // after the reviewer's unsaved corrections in the same request: the patch is all or nothing and applied in order, so
  // nothing typed is lost and row indexes still mean what the reviewer saw.
  const structure = useMutation({
    mutationFn: async (operation: FieldCorrection) => {
      const corrections = [...diffCorrections(specs, initial, form.getValues(), undefined, mapped), operation];
      return unwrap(await client.PATCH("/api/v1/documents/{id}/fields", { params: { path: { id: document.id } }, body: { corrections } }));
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKeys.documents.detail(document.id), updated);
      void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
    },
  });

  /** Map mode fills this field next: it is focused in the form and named in the preview. */
  const targetField = useCallback(
    (path: string | null) => {
      setMapTarget(path);
      if (path === null) return;
      const element = window.document.getElementById(fieldDomId(path));
      element?.scrollIntoView({ block: "nearest", behavior: "smooth" });
      element?.focus({ preventScroll: true });
    },
    [setMapTarget],
  );

  /** Enter in map mode: the value goes into the form with its source, and the next empty required field is next. */
  const assignMapped = useCallback(() => {
    const taken = map.take();
    if (!taken) return;
    form.setValue(formName(taken.path), taken.text, { shouldDirty: true });
    setMapped((current) => ({ ...current, [taken.path]: { text: taken.text, source: taken.source } }));
    const next = nextEmptyRequired(specs, form.getValues(), taken.path);
    targetField(next?.path ?? taken.path);
  }, [map, form, specs, targetField]);

  const skipMapped = useCallback(() => {
    map.cancel();
    const next = nextEmptyRequired(specs, form.getValues(), mapTarget);
    if (next) targetField(next.path);
  }, [map, specs, form, mapTarget, targetField]);

  const changeIdentifyMode = useCallback(
    (on: boolean) => {
      setIdentifyMode(on);
      if (on) {
        setMapMode(false);
        setTableMode(false);
        map.cancel();
        setTab("identify");
      } else identify.cancel();
    },
    [setIdentifyMode, setMapMode, setTableMode, map, setTab, identify],
  );

  const tools: PageViewerTools | undefined = mapAvailable || identifyAvailable
    ? {
        mapMode: mapMode && !identifyMode,
        onMapModeChange: mapAvailable
          ? (on) => {
              setMapMode(on);
              if (on) {
                setTableMode(false);
                setIdentifyMode(false);
              } else map.cancel();
            }
          : undefined,
        identifyMode,
        onIdentifyModeChange: identifyAvailable ? changeIdentifyMode : undefined,
        evidence,
        tableMode,
        onTableModeChange: tableAvailable
          ? (on) => {
              setTableMode(on);
              if (on) {
                map.cancel();
                setIdentifyMode(false);
                setTab("table");
              }
            }
          : undefined,
        onPick: (onPage, pick) => {
          if (identifyMode) {
            if (canMarkIdentifiers) identify.pick(onPage, pick);
            return;
          }
          if (tableMode) {
            if (pick.kind === "region") table.onRegion(onPage, pick.box);
            return;
          }
          if (!targetSpec) {
            toast.info(t("review.map.focusFirst"));
            return;
          }
          map.pick(onPage, pick);
        },
        selection: identifyMode ? (identify.proposal?.page === page ? (identify.proposal.words.length > 0 ? identify.proposal.words.map((w) => w.box) : [identify.proposal.box]) : undefined) : mapMode ? map.selectionBoxes(page) : undefined,
        table: tableMode || tab === "table" ? table.drawing(page) : null,
        banner: identifyMode ? undefined : mapMode ? (
          <MapPreview
            fieldLabel={targetSpec ? (targetSpec.path === targetSpec.fieldPath ? fieldLabel(targetSpec) : `${fieldLabel(targetSpec.fieldPath)} · ${fieldLabel(targetSpec)}`) : null}
            text={map.text}
            value={map.value}
            onValueChange={map.setValue}
            fits={map.fits}
            loading={map.loading}
            noWords={map.noWords}
            ocrPending={map.ocrPending}
            error={map.error}
            onAssign={assignMapped}
            onSkip={skipMapped}
            onCancel={map.cancel}
            onOcr={() => void map.ocr()}
          />
        ) : undefined,
      }
    : undefined;

  // Keyboard: Ctrl/Cmd+Enter approve, Esc back, j/k next/previous flag (outside inputs). A key that a dialog or a
  // popover handled (Radix marks its Esc with preventDefault) or that belongs to one is not the screen's. In map mode,
  // M toggles it and Esc first drops the selection.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.defaultPrevented) return;
      const dialogOpen = window.document.querySelector(OVERLAY_SELECTOR) !== null;
      if ((event.key === "m" || event.key === "M") && !event.ctrlKey && !event.metaKey && !event.altKey && !isEditableTarget(event.target) && !dialogOpen && mapAvailable) {
        event.preventDefault();
        setMapMode(!mapMode);
        if (!mapMode) setTableMode(false);
        else map.cancel();
        return;
      }
      if ((event.key === "i" || event.key === "I") && !event.ctrlKey && !event.metaKey && !event.altKey && !isEditableTarget(event.target) && !dialogOpen && identifyAvailable) {
        event.preventDefault();
        changeIdentifyMode(!identifyMode);
        return;
      }
      if (event.key === "Enter" && !dialogOpen && identifyMode && identify.proposal?.identifier && !isEditableTarget(event.target)) {
        event.preventDefault();
        identify.add();
        return;
      }
      if (event.key === "Escape" && !dialogOpen && identifyMode && identify.proposal) {
        event.preventDefault();
        identify.cancel();
        return;
      }
      if (event.key === "Escape" && !dialogOpen && mapMode && map.selection) {
        event.preventDefault();
        map.cancel();
        return;
      }
      if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
        if (!dialogOpen && canReview && (document.status === "NeedsReview" || document.status === "Validated") && !hasChanges) {
          event.preventDefault();
          setApproveOpen(true);
        }
        return;
      }
      if (event.key === "Escape" && !dialogOpen) {
        if (isEditableTarget(event.target)) {
          (event.target as HTMLElement).blur();
          return;
        }
        event.preventDefault();
        void navigate({ to: "/documents" });
        return;
      }
      if ((event.key === "j" || event.key === "k") && !event.ctrlKey && !event.metaKey && !event.altKey && !isEditableTarget(event.target) && !dialogOpen && flags.length > 0) {
        event.preventDefault();
        selectFlag(event.key === "j" ? Math.min(flags.length - 1, selectedFlag + 1) : Math.max(0, selectedFlag - 1));
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [canReview, document.status, hasChanges, flags, selectedFlag, selectFlag, navigate, mapAvailable, mapMode, map, setMapMode, setTableMode, identifyAvailable, identifyMode, identify, changeIdentifyMode]);

  const title = document.documentNumber ?? t("inbox.noNumber");
  const overlayLabel = useCallback((path: string) => fieldLabel(path, true), [fieldLabel]);

  return (
    <FormProvider {...form}>
      <div className="flex min-h-0 min-w-0 flex-1 flex-col gap-4" data-testid="review-screen" data-status={document.status} data-highlighted={highlightedPath ?? undefined}>
        <PageHeader
          // The shell keeps its actions on one line; here they wrap so a phone shows all of them.
          className="[&>div:last-child]:min-w-0 [&>div:last-child]:shrink [&>div:last-child]:flex-wrap"
          title={
            <span className="flex flex-wrap items-center gap-2">
              <Button variant="ghost" size="icon-sm" aria-label={t("review.back")} onClick={() => void navigate({ to: "/documents" })}>
                <ArrowLeft aria-hidden />
              </Button>
              <span data-testid="document-number" className="break-all">
                {title}
              </span>
              <StatusBadge status={document.status} />
              <Badge variant="outline" data-testid="document-type">
                {localized(document.definition?.name, locale) ?? t(`documentType.${document.type}`, { defaultValue: document.typeName ?? document.type })}
              </Badge>
              {/* type@version (docs/11 §19): which version of the type the form and the rules come from. */}
              {document.definition && (
                <Badge variant="secondary" data-testid="type-version" title={document.definition.key}>
                  {t("review.typeVersion", { version: document.definition.version })}
                </Badge>
              )}
              {/* The layout that identified and filled the document (docs/11 §19 "the layout and its version with an identification chip"). */}
              {identifiedLayout && (
                <Badge variant="outline" data-testid="layout-chip" title={t("review.layoutChipHint")}>
                  {t("review.layoutChip", { layout: identifiedLayout.layoutKey, version: identifiedLayout.version })}
                </Badge>
              )}
              {newLayout && (
                <Badge variant="warning" data-testid="new-layout-badge">
                  {t("review.newLayoutBadge")}
                </Badge>
              )}
            </span>
          }
          description={
            <span className="flex flex-wrap gap-x-4 gap-y-1">
              <span>{document.supplierName ?? t("review.noSupplier")}</span>
              <span>{formatDate(document.issueDate, locale)}</span>
              <span className="tabular-nums" data-testid="document-total">
                {formatMoney(document.totalGross, document.currency, locale)}
              </span>
            </span>
          }
          actions={
            canReview ? (
              <>
                {editable && (
                  <Button type="button" variant="secondary" onClick={() => void onSave()} disabled={!hasChanges || save.isPending} aria-busy={save.isPending} data-testid="save-button">
                    {save.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Save aria-hidden />}
                    {t("review.save.button")}
                  </Button>
                )}
                <ReviewActions
                  document={document}
                  approveOpen={approveOpen}
                  onApproveOpenChange={setApproveOpen}
                  hasUnsavedChanges={hasChanges}
                  locale={locale}
                  canManageSuppliers={canManageSuppliers}
                  canMarkGolden={!!document.supplierLayoutVersionId}
                  layoutSave={
                    learnable && layoutChoice
                      ? {
                          checked: layoutChoice.saveAsLayout,
                          onCheckedChange: (checked) => setLayoutChoice({ ...layoutChoice, saveAsLayout: checked }),
                          request: layoutRequest,
                          summary: layoutChoice.pendingLayoutVersionId
                            ? t("review.actions.saveAsLayoutPending")
                            : layoutChoice.saveAs === "newVersion"
                              ? t("review.actions.saveAsLayoutVersion")
                              : t("review.actions.saveAsLayoutNew", { layout: layoutChoice.layoutKey, rule: identify.rule ? t("review.actions.ruleMarked", { count: identification.chips.length }) : t("review.actions.ruleProposed") }),
                          onLearned: () =>
                            setSavedLayout({ supplierId: document.supplierId!, typeKey: document.definition?.key ?? document.type, layoutKey: layoutChoice.pendingLayoutVersionId ? null : layoutChoice.layoutKey.trim(), pendingLayoutVersionId: layoutChoice.pendingLayoutVersionId }),
                        }
                      : undefined
                  }
                />
              </>
            ) : undefined
          }
        />

        <ConfirmDialog
          open={leave.status === "blocked"}
          onOpenChange={(open) => {
            if (!open) leave.reset?.();
          }}
          title={t("review.leave.title")}
          description={t("review.leave.description")}
          confirmLabel={t("review.leave.confirm")}
          cancelLabel={t("review.leave.cancel")}
          destructive
          onConfirm={() => leave.proceed?.()}
        />

        {savedLayout && <LayoutSavedDialog saved={savedLayout} documentId={document.id} onClose={() => setSavedLayout(null)} />}

        {settled && (
          <Alert>
            <AlertTitle>{t("review.settledTitle")}</AlertTitle>
            <AlertDescription>{t("review.settledDescription", { status: t(`common:documentStatus.${document.status}`) })}</AlertDescription>
          </Alert>
        )}
        {document.status === "Failed" && document.failureReason && (
          <Alert variant="destructive">
            <AlertTitle>{t("review.failedTitle")}</AlertTitle>
            <AlertDescription className="break-words">{document.failureReason}</AlertDescription>
          </Alert>
        )}

        <section aria-labelledby="flags-heading" className="rounded-lg border p-3">
          <h2 id="flags-heading" className="mb-2 text-sm font-semibold">
            {t("review.flags.heading", { count: flags.length })}
            {/* Only with something to jump to; a phone has no j / k. */}
            {flags.length > 0 && <span className="ml-2 text-xs font-normal text-muted-foreground">{stacked ? t("review.flags.hintTouch") : t("review.flags.hint")}</span>}
          </h2>
          <FlagsList
            flags={flags}
            selectedIndex={selectedFlag}
            onSelect={setSelectedFlag}
            onJump={jumpToFlag}
            fieldLabel={(path) => fieldLabel(path, true)}
            validation={validation}
            locatedOnPage={pageCount > 0 ? locatedOnPage : undefined}
          />
          {/* Without an open flag, "Needs review" next to "every rule passed" needs its reason (a supplier always reviewed). */}
          {flags.length === 0 && document.status === "NeedsReview" && reviewReason && (
            <p className="mt-1 text-sm" data-testid="review-reason">
              {t("review.flags.reviewReason", { reason: reviewReasonLabels(reviewReason, t).join(", ") })}
            </p>
          )}
        </section>

        {/*
          A bounded height, so the page and the form scroll side by side (or one above the other) instead of the whole
          screen. It sits on a wrapper: the panel group sets an inline `height: 100%`, which beats a height class on it
          and, against a parent of auto height, let the group grow to the form's full length.
        */}
        <div className={cn("shrink-0 overflow-hidden rounded-lg border", stacked ? (pageCount > 0 ? "h-[max(160vh,48rem)]" : "h-[max(120vh,40rem)]") : "h-[max(80vh,32rem)]")} data-testid="review-panels-frame">
          <ResizablePanelGroup key={stacked ? "vertical" : "horizontal"} orientation={stacked ? "vertical" : "horizontal"} data-testid="review-panels">
            <ResizablePanel defaultSize={stacked ? (pageCount > 0 ? "40%" : "25%") : pageCount > 0 ? "45%" : "30%"} minSize={stacked ? "15%" : "20%"} data-testid="viewer-panel">
              <PageViewer
                documentId={document.id}
                pageCount={pageCount}
                boxes={boxes}
                highlightedPath={highlightedPath}
                onHover={setHoveredPath}
                onSelect={focusField}
                page={page}
                onPageChange={setPage}
                fieldLabel={overlayLabel}
                tools={tools}
              />
            </ResizablePanel>
            <ResizableHandle withHandle />
            <ResizablePanel defaultSize={stacked ? "60%" : pageCount > 0 ? "55%" : "70%"} minSize="30%">
              <Tabs value={(!showTableTab && tab === "table") || (!showDeliveriesTab && tab === "deliveries") || (!identifyAvailable && tab === "identify") ? "fields" : tab} onValueChange={setTab} className="flex h-full flex-col">
                <TabsList className="m-2 w-fit">
                  <TabsTrigger value="fields">{t("review.tabs.fields")}</TabsTrigger>
                  {showTableTab && (
                    <TabsTrigger value="table" data-testid="table-tab">
                      {t("review.tabs.table")}
                    </TabsTrigger>
                  )}
                  {identifyAvailable && (
                    <TabsTrigger value="identify" data-testid="identify-tab">
                      {t("review.tabs.identify")}
                    </TabsTrigger>
                  )}
                  <TabsTrigger value="timeline">{t("review.tabs.timeline")}</TabsTrigger>
                  {showDeliveriesTab && (
                    <TabsTrigger value="deliveries" data-testid="deliveries-tab-trigger">
                      {t("review.tabs.deliveries")}
                    </TabsTrigger>
                  )}
                  <TabsTrigger value="original">{t("review.tabs.original")}</TabsTrigger>
                </TabsList>
                {/* A plain scroller: Radix ScrollArea sizes its content as a table, so the form grew to its max-content width. */}
                <div className="@container relative min-h-0 min-w-0 flex-1 overflow-y-auto">
                  <TabsContent value="fields" className="p-4">
                    {isCorrectable(document.status) && openFlag(document, "REVISION_OF") && (
                      <div className="mb-6">
                        <RevisionPanel document={document} canReview={canReview} />
                      </div>
                    )}
                    {openFlag(document, "EXTERNAL_REJECTED") && (
                      <div className="mb-6">
                        <IntegrationIssuePanel document={document} canReview={canReview} />
                      </div>
                    )}
                    {layoutFlag && layoutChoice !== null && isCorrectable(document.status) && (
                      <div className="mb-6">
                        <NewLayoutPanel document={document} flag={layoutFlag} locale={locale} choice={layoutChoice} onChoiceChange={setLayoutChoice} fieldLabel={(path) => (path.includes(".") ? `${fieldLabel(path.slice(0, path.indexOf(".")), true)} · ${fieldLabel(path, true)}` : fieldLabel(path, true))} canReview={canReview} />
                      </div>
                    )}
                    {layoutFlag?.code === "LAYOUT_AMBIGUOUS" && layoutChoice === null && isCorrectable(document.status) && (
                      <div className="mb-6">
                        <NewLayoutPanel document={document} flag={layoutFlag} locale={locale} choice={defaultLayoutChoice(document, null, [])} onChoiceChange={() => undefined} fieldLabel={(path) => fieldLabel(path, true)} canReview={false} />
                      </div>
                    )}
                    {hasCanonical ? (
                      <form
                        onSubmit={(event) => void onSave(event)}
                        className="flex flex-col gap-6"
                        aria-label={t("review.form.label")}
                        data-editable={editable}
                        onFocusCapture={(event) => {
                          // The field map mode fills: the one the reviewer focused last (it stays the target when the
                          // focus moves on to the page).
                          const path = (event.target as HTMLElement).closest?.("[data-path]")?.getAttribute("data-path");
                          if (path && specs.some((s) => s.path === path)) setMapTarget(path);
                        }}
                      >
                        <DefinitionForm
                          layout={layout!}
                          canonical={canonical}
                          disabled={!editable}
                          threshold={threshold}
                          onHighlight={highlightField}
                          flaggedPaths={flaggedPaths}
                          onStructuralChange={editable ? (operation) => structure.mutate(operation) : undefined}
                          onMove={editable ? (operation) => structure.mutate(operation) : undefined}
                          busy={structure.isPending || save.isPending}
                        />
                        {editable && (
                          <div className="flex justify-end">
                            <Button type="submit" variant="secondary" disabled={!hasChanges || save.isPending}>
                              <Save aria-hidden />
                              {t("review.save.button")}
                            </Button>
                          </div>
                        )}
                      </form>
                    ) : canReview && isCorrectable(document.status) && document.configurableTypes === true ? (
                      // docs/11 §12.1: a document without a form or data gets the "Map this document" panel, on a tenant
                      // with configurableTypes (principle 6); any other tenant keeps the empty state it had before P2.
                      <MapDocumentPanel document={document} locale={locale} hasForm={!!formSpec} />
                    ) : (
                      <EmptyState title={t("review.noCanonicalTitle")} description={isInFlight(document.status) ? t("review.noCanonicalProcessing") : formSpec ? t("review.noCanonicalExtracted") : t("review.noCanonical")} />
                    )}
                  </TabsContent>
                  {showTableTab && (
                    <TabsContent value="table" className="p-4">
                      <TableToolPanel
                        state={table.state}
                        rowFields={table.rowFields}
                        hasTiers={table.hasTiers}
                        hasCategory={table.hasCategory}
                        report={table.report}
                        reportKind={table.reportKind}
                        currentPage={page}
                        proposing={table.proposing}
                        previewing={table.previewing}
                        writing={table.writing}
                        canWrite={editable && !hasChanges}
                        error={table.error}
                        onColumnTarget={table.onColumnTarget}
                        onRemoveDivider={table.onRemoveDivider}
                        onAllPagesChange={table.onAllPagesChange}
                        onGroupHeadingsChange={table.onGroupHeadingsChange}
                        onSkipLinesChange={table.onSkipLinesChange}
                        onStopLinesChange={table.onStopLinesChange}
                        onMarginsChange={table.onMarginsChange}
                        onPreview={table.onPreview}
                        onWrite={table.onWrite}
                        onReset={table.onReset}
                        onPickPage={setPage}
                        onPickRejected={(line) => setPage(Number(line.page))}
                      />
                      {!tableMode && tableAvailable && (
                        <Button type="button" variant="outline" className="mt-4" onClick={() => tools?.onTableModeChange?.(true)} data-testid="table-tool-start">
                          {t("review.table.start")}
                        </Button>
                      )}
                    </TabsContent>
                  )}
                  {identifyAvailable && (
                    <TabsContent value="identify" className="p-4">
                      <IdentificationCard
                        draft={identification}
                        onDraftChange={setIdentification}
                        proposal={identify.proposal}
                        onAdd={identify.add}
                        onCancel={identify.cancel}
                        onUpdate={identify.update}
                        here={identify.here}
                        recent={identify.recent}
                        documentId={document.id}
                        active={identifyMode}
                        onActivate={() => changeIdentifyMode(true)}
                        identifiedBy={identifiedLayout && !learnable ? `${identifiedLayout.layoutKey} v${identifiedLayout.version}` : null}
                        canMark={canMarkIdentifiers}
                        footer={
                          tenantRuleMarkable ? (
                            <TenantRuleAction
                              documentId={document.id}
                              typeKey={document.definition?.key ?? document.type}
                              rule={identify.rule as Record<string, unknown> | null}
                              holds={identify.here.data?.documents.find((d) => d.documentId === document.id)?.matched === true}
                            />
                          ) : undefined
                        }
                      />
                    </TabsContent>
                  )}
                  <TabsContent value="timeline" className="p-4">
                    <Timeline documentId={document.id} enabled={tab === "timeline"} />
                  </TabsContent>
                  {showDeliveriesTab && (
                    <TabsContent value="deliveries" className="p-4">
                      <DeliveriesTab documentId={document.id} enabled={tab === "deliveries"} canRedeliver={canRedeliver} settledAt={settledAt} rejection={openFlag(document, "EXTERNAL_REJECTED")} />
                    </TabsContent>
                  )}
                  <TabsContent value="original" className="p-4">
                    <OriginalTab document={document} />
                  </TabsContent>
                </div>
              </Tabs>
            </ResizablePanel>
          </ResizablePanelGroup>
        </div>
      </div>
    </FormProvider>
  );
}
