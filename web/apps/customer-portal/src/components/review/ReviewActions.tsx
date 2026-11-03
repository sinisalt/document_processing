import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Check, RotateCcw, X } from "lucide-react";
import { Button, Checkbox, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Textarea } from "@docproc/ui";
import { ConfirmDialog, canReprocessFrom, defaultReprocessStage, furthestPipelineStatus, queryKeys, reprocessStages, unwrap, useApi, type ReprocessStage } from "@docproc/app-shell";
import { isApplePlatform } from "@/lib/platform";
import { useDocumentEvents, type DocumentDetail } from "@/lib/queries";

/** Where `:approve` / `:reject` are allowed (docs/05). */
export function canDecide(status: DocumentDetail["status"]): boolean {
  return status === "NeedsReview" || status === "Validated";
}

/** Where `:reprocess` is allowed (`DocumentStateMachine.CanReprocess`). */
export function canReprocess(status: DocumentDetail["status"]): boolean {
  return status === "Failed" || status === "NeedsReview" || status === "Rejected" || status === "Validated";
}

export interface ReviewActionsProps {
  document: DocumentDetail;
  /** The approve dialog is opened from outside too (Ctrl/Cmd+Enter). */
  approveOpen: boolean;
  onApproveOpenChange: (open: boolean) => void;
  /** Unsaved corrections block approve, reject and reprocess, so no action discards what the reviewer typed. */
  hasUnsavedChanges: boolean;
  onDecided?: () => void;
}

/** Approve, Reject (with a required reason) and Reprocess (from a stage) with their confirmations. */
export function ReviewActions({ document, approveOpen, onApproveOpenChange, hasUnsavedChanges, onDecided }: ReviewActionsProps) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [rejectOpen, setRejectOpen] = useState(false);
  const [reason, setReason] = useState("");
  const [reprocessOpen, setReprocessOpen] = useState(false);
  // The API refuses a stage the document never reached (409 "Stage not reached"): offer what the timeline proves, and
  // default to the latest of it. A timeline that does not load leaves what the status proves.
  const timeline = useDocumentEvents(document.id, reprocessOpen);
  const furthest = timeline.data ? furthestPipelineStatus(timeline.data, document.status) : timeline.isError ? furthestPipelineStatus([], document.status) : null;
  const [picked, setPicked] = useState<ReprocessStage | null>(null);
  // A document keeps the type version it was read with (docs/11 §15.1); a reprocess can take the published one instead
  // (`configMode: current`), e.g. after the tenant added fields to the type. Offered when the document has a definition.
  const definition = document.definition ?? null;
  const typeQuery = useQuery({
    queryKey: queryKeys.documentTypes.detail(definition?.key ?? ""),
    queryFn: async () => unwrap(await client.GET("/api/v1/document-types/{key}", { params: { path: { key: definition!.key } } })),
    enabled: reprocessOpen && definition !== null,
  });
  const published = typeQuery.data?.publishedVersion == null ? null : Number(typeQuery.data.publishedVersion);
  const newerPublished = definition !== null && published !== null && published !== definition.version;
  const [currentPicked, setCurrentPicked] = useState<boolean | null>(null);
  const useCurrent = definition !== null && (currentPicked ?? newerPublished);
  // The fields a newer version adds are read in Extract, so taking the published version defaults to Extract.
  const fallbackStage = (reached: NonNullable<typeof furthest>): ReprocessStage => (useCurrent && canReprocessFrom("Extract", reached) ? "Extract" : defaultReprocessStage(reached));
  const stage: ReprocessStage | null = furthest === null ? null : picked !== null && canReprocessFrom(picked, furthest) ? picked : fallbackStage(furthest);

  const settle = (updated: DocumentDetail, message: string) => {
    queryClient.setQueryData(queryKeys.documents.detail(document.id), updated);
    void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
    void queryClient.invalidateQueries({ queryKey: queryKeys.reviewTasks.all });
    toast.success(message);
    onDecided?.();
  };

  const approve = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/documents/{id}:approve", { params: { path: { id: document.id } }, body: { comment: null } })),
    onSuccess: (updated) => settle(updated, t("review.actions.approved")),
  });
  const reject = useMutation({
    mutationFn: async (why: string) => unwrap(await client.POST("/api/v1/documents/{id}:reject", { params: { path: { id: document.id } }, body: { reason: why } })),
    onSuccess: (updated) => {
      setReason("");
      settle(updated, t("review.actions.rejected"));
    },
  });
  const reprocess = useMutation({
    mutationFn: async (fromStage: ReprocessStage) =>
      unwrap(
        await client.POST("/api/v1/documents/{id}:reprocess", {
          params: { path: { id: document.id } },
          body: definition === null ? { fromStage } : { fromStage, configMode: useCurrent ? "current" : "pinned" },
        }),
      ),
    onSuccess: (updated, fromStage) => settle(updated, t("review.actions.reprocessing", { stage: t(`stages.${fromStage}`) })),
  });

  const decidable = canDecide(document.status) && !hasUnsavedChanges;
  const apple = isApplePlatform();

  return (
    <div className="flex flex-wrap items-center gap-2" role="group" aria-label={t("review.actions.label")}>
      <ConfirmDialog
        open={approveOpen}
        onOpenChange={onApproveOpenChange}
        title={t("review.actions.approveTitle")}
        description={t("review.actions.approveDescription", { number: document.documentNumber ?? document.id })}
        confirmLabel={t("review.actions.approve")}
        onConfirm={() => approve.mutateAsync().then(() => undefined)}
        isPending={approve.isPending}
        trigger={
          <Button disabled={!decidable} title={hasUnsavedChanges ? t("review.actions.saveFirst") : undefined} aria-keyshortcuts={apple ? "Meta+Enter" : "Control+Enter"} data-testid="approve-button">
            <Check aria-hidden />
            {t("review.actions.approve")}
            <kbd className="ml-1 hidden rounded border border-primary-foreground/40 bg-primary-foreground/10 px-1 text-[10px] font-medium text-primary-foreground lg:inline" aria-hidden data-testid="approve-shortcut">
              {apple ? t("review.actions.shortcutApple") : t("review.actions.shortcutOther")}
            </kbd>
          </Button>
        }
      />
      <ConfirmDialog
        open={rejectOpen}
        onOpenChange={setRejectOpen}
        title={t("review.actions.rejectTitle")}
        description={t("review.actions.rejectDescription")}
        confirmLabel={t("review.actions.reject")}
        destructive
        confirmDisabled={reason.trim().length === 0}
        onConfirm={() => reject.mutateAsync(reason.trim()).then(() => undefined)}
        isPending={reject.isPending}
        trigger={
          <Button variant="outline" disabled={!decidable} title={hasUnsavedChanges ? t("review.actions.saveFirst") : undefined} data-testid="reject-button">
            <X aria-hidden />
            {t("review.actions.reject")}
          </Button>
        }
      >
        <div className="flex flex-col gap-1">
          <Label htmlFor="reject-reason">{t("review.actions.rejectReason")}</Label>
          <Textarea id="reject-reason" value={reason} onChange={(event) => setReason(event.target.value)} required rows={3} autoFocus placeholder={t("review.actions.rejectReasonPlaceholder")} />
        </div>
      </ConfirmDialog>
      <ConfirmDialog
        open={reprocessOpen}
        onOpenChange={setReprocessOpen}
        title={t("review.actions.reprocessTitle")}
        description={t("review.actions.reprocessDescription")}
        confirmLabel={t("review.actions.reprocess")}
        onConfirm={() => (stage === null ? undefined : reprocess.mutateAsync(stage).then(() => undefined))}
        isPending={reprocess.isPending}
        confirmDisabled={stage === null}
        trigger={
          <Button variant="ghost" disabled={!canReprocess(document.status) || hasUnsavedChanges} title={hasUnsavedChanges ? t("review.actions.saveFirst") : undefined} data-testid="reprocess-button">
            <RotateCcw aria-hidden />
            {t("review.actions.reprocess")}
          </Button>
        }
      >
        <div className="flex flex-col gap-1">
          <Label htmlFor="reprocess-stage">{t("review.actions.fromStage")}</Label>
          <Select value={stage ?? ""} onValueChange={(value) => setPicked(value as ReprocessStage)} disabled={stage === null}>
            <SelectTrigger id="reprocess-stage">
              <SelectValue placeholder={t("review.actions.fromStageLoading")} />
            </SelectTrigger>
            <SelectContent>
              {reprocessStages.map((s) => (
                <SelectItem key={s} value={s} disabled={furthest === null || !canReprocessFrom(s, furthest)}>
                  {t(`stages.${s}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        {definition !== null && (
          <div className="flex items-start gap-2" data-testid="reprocess-config-mode">
            <Checkbox id="reprocess-current" checked={useCurrent} onCheckedChange={(checked) => setCurrentPicked(checked === true)} className="mt-0.5" />
            <div className="grid gap-1">
              <Label htmlFor="reprocess-current">{t("review.actions.useCurrentVersion")}</Label>
              {published !== null && (
                <p className="text-xs text-muted-foreground">
                  {newerPublished
                    ? t("review.actions.useCurrentVersionNewer", { type: document.typeName ?? definition.key, version: definition.version, published })
                    : t("review.actions.useCurrentVersionSame", { type: document.typeName ?? definition.key, version: definition.version })}
                </p>
              )}
            </div>
          </div>
        )}
      </ConfirmDialog>
    </div>
  );
}
