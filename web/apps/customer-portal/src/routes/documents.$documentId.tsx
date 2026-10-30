import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { FormProvider, useForm } from "react-hook-form";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft, Loader2, Save } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Button, ResizableHandle, ResizablePanel, ResizablePanelGroup, ScrollArea, Skeleton, Tabs, TabsContent, TabsList, TabsTrigger } from "@docproc/ui";
import {
  ApiError,
  EmptyState,
  PageHeader,
  Policies,
  StatusBadge,
  formatDate,
  formatMoney,
  isCorrectable,
  isInFlight,
  queryKeys,
  unwrap,
  useApi,
  useAuth,
} from "@docproc/app-shell";
import { CanonicalForm, useFieldLabel } from "@/components/review/CanonicalForm";
import { FlagsList, sortFlags } from "@/components/review/FlagsList";
import { LineItemGrid } from "@/components/review/LineItemGrid";
import { OriginalTab } from "@/components/review/OriginalTab";
import { PageViewer } from "@/components/review/PageViewer";
import { ReviewActions } from "@/components/review/ReviewActions";
import { Timeline } from "@/components/review/Timeline";
import { allSpecs, fieldDomId, formName, toFormValues, type CanonicalInvoice } from "@/lib/canonical";
import { diffCorrections } from "@/lib/corrections";
import { collectProvenanceBoxes } from "@/lib/overlay";
import { useDocument, useTenantSettings, type DocumentDetail, type DocumentFlag } from "@/lib/queries";

export const Route = createFileRoute("/documents/$documentId")({ component: ReviewPage });

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName) || target.getAttribute("role") === "combobox";
}

function ReviewPage() {
  const { documentId } = Route.useParams();
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const { hasPolicy, user } = useAuth();
  const document = useDocument(documentId);
  const settings = useTenantSettings(user?.scope === "tenant");

  if (document.isPending) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-10 w-1/2" />
        <Skeleton className="h-96" />
      </div>
    );
  }
  if (document.isError || !document.data) {
    const status = document.error instanceof ApiError ? document.error.status : undefined;
    return (
      <EmptyState
        title={status === 404 ? t("review.notFound") : t("common:errors.title")}
        description={status === 404 ? t("review.notFoundDescription") : document.error instanceof Error ? document.error.message : undefined}
        action={
          <Button variant="outline" onClick={() => void navigate({ to: "/documents" })}>
            <ArrowLeft aria-hidden />
            {t("review.back")}
          </Button>
        }
      />
    );
  }

  const threshold = Number(settings.data?.settings.autoApproveConfidenceThreshold ?? 0.8);
  return <ReviewScreen key={`${document.data.id}:${document.data.updatedAt}`} document={document.data} canReview={hasPolicy(Policies.Review)} threshold={threshold} locale={i18n.language} />;
}

interface ReviewScreenProps {
  document: DocumentDetail;
  canReview: boolean;
  threshold: number;
  locale: string;
}

/** Remounted (by key) whenever the server's version of the document changes, so the form always starts from it. */
function ReviewScreen({ document, canReview, threshold, locale }: ReviewScreenProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const fieldLabel = useFieldLabel();

  const hasCanonical = (document.type === "Invoice" || document.type === "CreditNote") && !!document.canonical;
  const canonical = useMemo(() => (hasCanonical ? (document.canonical as CanonicalInvoice) : null), [document.canonical, hasCanonical]);
  const specs = useMemo(() => allSpecs(canonical), [canonical]);
  const initial = useMemo(() => toFormValues(canonical, specs), [canonical, specs]);
  const boxes = useMemo(() => collectProvenanceBoxes(canonical), [canonical]);
  const flags = useMemo(() => sortFlags(document.flags), [document.flags]);
  const flaggedLines = useMemo(() => new Set(flags.map((f) => /^lines\[(\d+)\]/.exec(f.field ?? "")?.[1]).filter((v): v is string => !!v).map(Number)), [flags]);

  const editable = canReview && isCorrectable(document.status) && hasCanonical;
  const settled = !isCorrectable(document.status) && !isInFlight(document.status);

  const form = useForm<Record<string, unknown>>({ defaultValues: initial, mode: "onBlur" });
  const { isDirty } = form.formState;

  const [highlightedPath, setHighlightedPath] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [tab, setTab] = useState("fields");
  const [approveOpen, setApproveOpen] = useState(false);
  const [selectedFlag, setSelectedFlag] = useState(-1);

  const pageCount = Number(document.pageCount) || 0;

  /** Focus the input of a canonical path (or the first input inside a block field) and show the page it was read from. */
  const focusField = useCallback(
    (path: string) => {
      const spec = specs.find((s) => s.path === path) ?? specs.find((s) => s.fieldPath === path) ?? specs.find((s) => s.path.startsWith(`${path}.`) || s.path.startsWith(`${path}[`));
      const box = boxes.find((b) => b.path === path) ?? (spec ? boxes.find((b) => b.path === spec.fieldPath) : undefined);
      if (box) setPage(box.page);
      setHighlightedPath(spec?.fieldPath ?? path);
      setTab("fields");
      const target = spec ? window.document.getElementById(fieldDomId(spec.path)) : null;
      if (target) {
        target.scrollIntoView({ block: "center", behavior: "smooth" });
        target.focus({ preventScroll: true });
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

  // Keyboard: Ctrl/Cmd+Enter approve, Esc back, j/k next/previous flag (outside inputs).
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
        if (canReview && (document.status === "NeedsReview" || document.status === "Validated") && !isDirty) {
          event.preventDefault();
          setApproveOpen(true);
        }
        return;
      }
      const dialogOpen = window.document.querySelector('[role="dialog"], [role="alertdialog"]') !== null;
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
        const next = event.key === "j" ? Math.min(flags.length - 1, selectedFlag + 1) : Math.max(0, selectedFlag - 1);
        setSelectedFlag(next);
        const flag = flags[next];
        if (flag) jumpToFlag(flag);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [canReview, document.status, isDirty, flags, selectedFlag, jumpToFlag, navigate]);

  const title = document.documentNumber ?? t("inbox.noNumber");

  return (
    <FormProvider {...form}>
      <div className="flex min-h-0 flex-1 flex-col gap-4" data-testid="review-screen" data-status={document.status}>
        <PageHeader
          title={
            <span className="flex flex-wrap items-center gap-2">
              <Button variant="ghost" size="icon-sm" aria-label={t("review.back")} onClick={() => void navigate({ to: "/documents" })}>
                <ArrowLeft aria-hidden />
              </Button>
              <span data-testid="document-number">{title}</span>
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
                  <Button type="button" variant="secondary" onClick={() => void onSave()} disabled={!isDirty || save.isPending} aria-busy={save.isPending} data-testid="save-button">
                    {save.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Save aria-hidden />}
                    {t("review.save.button")}
                  </Button>
                )}
                <ReviewActions document={document} approveOpen={approveOpen} onApproveOpenChange={setApproveOpen} hasUnsavedChanges={isDirty} />
              </>
            ) : undefined
          }
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
            <AlertDescription>{document.failureReason}</AlertDescription>
          </Alert>
        )}

        <section aria-labelledby="flags-heading" className="rounded-lg border p-3">
          <h2 id="flags-heading" className="mb-2 text-sm font-semibold">
            {t("review.flags.heading", { count: flags.length })}
            <span className="ml-2 text-xs font-normal text-muted-foreground">{t("review.flags.hint")}</span>
          </h2>
          <FlagsList flags={flags} selectedIndex={selectedFlag} onSelect={setSelectedFlag} onJump={jumpToFlag} fieldLabel={(path) => fieldLabel(path, true)} />
        </section>

        <ResizablePanelGroup orientation="horizontal" className="min-h-[60vh] flex-1 rounded-lg border">
          <ResizablePanel defaultSize={45} minSize={25}>
            <PageViewer documentId={document.id} pageCount={pageCount} boxes={boxes} highlightedPath={highlightedPath} onHover={setHighlightedPath} onSelect={focusField} page={page} onPageChange={setPage} />
          </ResizablePanel>
          <ResizableHandle withHandle />
          <ResizablePanel defaultSize={55} minSize={30}>
            <Tabs value={tab} onValueChange={setTab} className="flex h-full flex-col">
              <TabsList className="m-2 w-fit">
                <TabsTrigger value="fields">{t("review.tabs.fields")}</TabsTrigger>
                <TabsTrigger value="timeline">{t("review.tabs.timeline")}</TabsTrigger>
                <TabsTrigger value="original">{t("review.tabs.original")}</TabsTrigger>
              </TabsList>
              <ScrollArea className="min-h-0 flex-1">
                <TabsContent value="fields" className="p-4">
                  {hasCanonical ? (
                    <form onSubmit={(event) => void onSave(event)} className="flex flex-col gap-6" aria-label={t("review.form.label")} data-editable={editable}>
                      <CanonicalForm canonical={canonical} disabled={!editable} threshold={threshold} onHighlight={setHighlightedPath} />
                      <LineItemGrid canonical={canonical} disabled={!editable} threshold={threshold} onHighlight={setHighlightedPath} flaggedLines={flaggedLines} />
                      {editable && (
                        <div className="flex justify-end">
                          <Button type="submit" variant="secondary" disabled={!isDirty || save.isPending}>
                            <Save aria-hidden />
                            {t("review.save.button")}
                          </Button>
                        </div>
                      )}
                    </form>
                  ) : (
                    <EmptyState title={t("review.noCanonicalTitle")} description={isInFlight(document.status) ? t("review.noCanonicalProcessing") : t("review.noCanonical")} />
                  )}
                </TabsContent>
                <TabsContent value="timeline" className="p-4">
                  <Timeline documentId={document.id} enabled={tab === "timeline"} />
                </TabsContent>
                <TabsContent value="original" className="p-4">
                  <OriginalTab document={document} />
                </TabsContent>
              </ScrollArea>
            </Tabs>
          </ResizablePanel>
        </ResizablePanelGroup>
      </div>
    </FormProvider>
  );
}
