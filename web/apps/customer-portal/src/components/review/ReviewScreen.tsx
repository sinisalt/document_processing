import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useBlocker, useNavigate } from "@tanstack/react-router";
import { FormProvider, useForm } from "react-hook-form";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft, Loader2, Save } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Button, cn, ResizableHandle, ResizablePanel, ResizablePanelGroup, Tabs, TabsContent, TabsList, TabsTrigger, useIsMobile } from "@docproc/ui";
import { ApiError, ConfirmDialog, EmptyState, PageHeader, StatusBadge, formatDate, formatMoney, isCorrectable, isInFlight, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { CanonicalForm, useFieldLabel } from "./CanonicalForm";
import type { HighlightReason } from "./FieldInput";
import { FlagsList, sortFlags } from "./FlagsList";
import { LineItemGrid } from "./LineItemGrid";
import { OriginalTab } from "./OriginalTab";
import { PageViewer } from "./PageViewer";
import { ReviewActions } from "./ReviewActions";
import { Timeline } from "./Timeline";
import { allSpecs, fieldDomId, formName, toFormValues, type CanonicalInvoice } from "@/lib/canonical";
import { diffCorrections } from "@/lib/corrections";
import { collectProvenanceBoxes } from "@/lib/overlay";
import { useDocumentEvents, type DocumentDetail, type DocumentFlag } from "@/lib/queries";
import { validationState } from "@/lib/validation";

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
}

/** Remounted (by key) whenever the server's version of the document changes, so the form always starts from it. */
export function ReviewScreen({ document, canReview, threshold, locale }: ReviewScreenProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const fieldLabel = useFieldLabel();
  // Below md (768 px) the viewer sits above the form instead of beside it: side by side, neither fits a phone.
  const stacked = useIsMobile();

  const hasCanonical = (document.type === "Invoice" || document.type === "CreditNote") && !!document.canonical;
  const canonical = useMemo(() => (hasCanonical ? (document.canonical as CanonicalInvoice) : null), [document.canonical, hasCanonical]);
  const specs = useMemo(() => allSpecs(canonical), [canonical]);
  const initial = useMemo(() => toFormValues(canonical, specs), [canonical, specs]);
  const boxes = useMemo(() => collectProvenanceBoxes(canonical), [canonical]);
  const flags = useMemo(() => sortFlags(document.flags), [document.flags]);
  const flaggedLines = useMemo(() => new Set(flags.map((f) => /^lines\[(\d+)\]/.exec(f.field ?? "")?.[1]).filter((v): v is string => !!v).map(Number)), [flags]);
  const flaggedPaths = useMemo(() => flags.map((f) => f.field).filter((path): path is string => !!path), [flags]);
  /** A flag's field has a box when the field, or the block it belongs to (`supplier` for `supplier.vatId`), has one. */
  const locatedOnPage = useCallback((path: string) => boxes.some((box) => box.path === path || path.startsWith(`${box.path}.`) || path.startsWith(`${box.path}[`)), [boxes]);

  // An empty flag list only means "every rule passed" when Validate ran on what is on screen; the timeline tells. The
  // screen remounts for every server version of the document, so it reads the timeline afresh each time: a cached one
  // can predate the Validate run that produced this version.
  const events = useDocumentEvents(document.id, flags.length === 0, { refetchOnMount: "always" });
  const validation = validationState(document.status, events.data);

  const editable = canReview && isCorrectable(document.status) && hasCanonical;
  const settled = !isCorrectable(document.status) && !isInFlight(document.status);

  const form = useForm<Record<string, unknown>>({ defaultValues: initial, mode: "onBlur" });
  // "Unsaved" means a correction would be sent, not that an input was touched: "58,80" for 58.8 changes nothing.
  const [hasChanges, setHasChanges] = useState(false);
  useEffect(() => {
    const subscription = form.watch((values) => setHasChanges(diffCorrections(specs, initial, values).length > 0));
    return () => subscription.unsubscribe();
  }, [form, specs, initial]);

  // Leaving with unsaved corrections asks first: the back button, Esc, the sidebar, browser Back, and closing the tab.
  const leave = useBlocker({ shouldBlockFn: blockAlways, enableBeforeUnload: hasChanges, disabled: !hasChanges, withResolver: true });

  const [highlightedPath, setHighlightedPath] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [tab, setTab] = useState("fields");
  const [approveOpen, setApproveOpen] = useState(false);
  const [selectedFlag, setSelectedFlag] = useState(-1);

  const pageCount = Number(document.pageCount) || 0;

  /** Hover and focus in the form highlight the field's box; focus also turns to its page, so the box is in sight. */
  const highlightField = useCallback(
    (path: string | null, reason?: HighlightReason) => {
      setHighlightedPath(path);
      if (reason !== "focus" || path === null) return;
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
      setHighlightedPath(spec?.fieldPath ?? path);
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
      const corrections = diffCorrections(specs, initial, values);
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

  // Keyboard: Ctrl/Cmd+Enter approve, Esc back, j/k next/previous flag (outside inputs). A key that a dialog or a
  // popover handled (Radix marks its Esc with preventDefault) or that belongs to one is not the screen's.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.defaultPrevented) return;
      const dialogOpen = window.document.querySelector(OVERLAY_SELECTOR) !== null;
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
  }, [canReview, document.status, hasChanges, flags, selectedFlag, selectFlag, navigate]);

  const title = document.documentNumber ?? t("inbox.noNumber");
  const overlayLabel = useCallback((path: string) => fieldLabel(path, true), [fieldLabel]);

  return (
    <FormProvider {...form}>
      <div className="flex min-h-0 min-w-0 flex-1 flex-col gap-4" data-testid="review-screen" data-status={document.status}>
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
              <Badge variant="outline">{t(`documentType.${document.type}`)}</Badge>
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
                <ReviewActions document={document} approveOpen={approveOpen} onApproveOpenChange={setApproveOpen} hasUnsavedChanges={hasChanges} />
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
            <span className="ml-2 text-xs font-normal text-muted-foreground">{t("review.flags.hint")}</span>
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
        </section>

        {/*
          A bounded height, so the page and the form scroll side by side (or one above the other) instead of the whole
          screen. It sits on a wrapper: the panel group sets an inline `height: 100%`, which beats a height class on it
          and, against a parent of auto height, let the group grow to the form's full length.
        */}
        <div className={cn("shrink-0 overflow-hidden rounded-lg border", stacked ? (pageCount > 0 ? "h-[max(160vh,48rem)]" : "h-[max(120vh,40rem)]") : "h-[max(80vh,32rem)]")} data-testid="review-panels-frame">
          <ResizablePanelGroup key={stacked ? "vertical" : "horizontal"} orientation={stacked ? "vertical" : "horizontal"} data-testid="review-panels">
            <ResizablePanel defaultSize={stacked ? (pageCount > 0 ? "40%" : "25%") : pageCount > 0 ? "45%" : "30%"} minSize={stacked ? "15%" : "20%"} data-testid="viewer-panel">
              <PageViewer documentId={document.id} pageCount={pageCount} boxes={boxes} highlightedPath={highlightedPath} onHover={setHighlightedPath} onSelect={focusField} page={page} onPageChange={setPage} fieldLabel={overlayLabel} />
            </ResizablePanel>
            <ResizableHandle withHandle />
            <ResizablePanel defaultSize={stacked ? "60%" : pageCount > 0 ? "55%" : "70%"} minSize="30%">
              <Tabs value={tab} onValueChange={setTab} className="flex h-full flex-col">
                <TabsList className="m-2 w-fit">
                  <TabsTrigger value="fields">{t("review.tabs.fields")}</TabsTrigger>
                  <TabsTrigger value="timeline">{t("review.tabs.timeline")}</TabsTrigger>
                  <TabsTrigger value="original">{t("review.tabs.original")}</TabsTrigger>
                </TabsList>
                {/* A plain scroller: Radix ScrollArea sizes its content as a table, so the form grew to its max-content width. */}
                <div className="@container relative min-h-0 min-w-0 flex-1 overflow-y-auto">
                  <TabsContent value="fields" className="p-4">
                    {hasCanonical ? (
                      <form onSubmit={(event) => void onSave(event)} className="flex flex-col gap-6" aria-label={t("review.form.label")} data-editable={editable}>
                        <CanonicalForm canonical={canonical} disabled={!editable} threshold={threshold} onHighlight={highlightField} />
                        <LineItemGrid canonical={canonical} disabled={!editable} threshold={threshold} onHighlight={highlightField} flaggedLines={flaggedLines} flaggedPaths={flaggedPaths} />
                        {editable && (
                          <div className="flex justify-end">
                            <Button type="submit" variant="secondary" disabled={!hasChanges || save.isPending}>
                              <Save aria-hidden />
                              {t("review.save.button")}
                            </Button>
                          </div>
                        )}
                      </form>
                    ) : (
                      <EmptyState title={t("review.noCanonicalTitle")} description={isInFlight(document.status) ? t("review.noCanonicalProcessing") : document.type === "Invoice" || document.type === "CreditNote" ? t("review.noCanonicalExtracted") : t("review.noCanonical")} />
                    )}
                  </TabsContent>
                  <TabsContent value="timeline" className="p-4">
                    <Timeline documentId={document.id} enabled={tab === "timeline"} />
                  </TabsContent>
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
