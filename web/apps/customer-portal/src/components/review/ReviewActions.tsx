import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Check, RotateCcw, X } from "lucide-react";
import { Button, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Textarea } from "@docproc/ui";
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
  const stage: ReprocessStage | null = furthest === null ? null : picked !== null && canReprocessFrom(picked, furthest) ? picked : defaultReprocessStage(furthest);

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
    mutationFn: async (fromStage: ReprocessStage) => unwrap(await client.POST("/api/v1/documents/{id}:reprocess", { params: { path: { id: document.id } }, body: { fromStage } })),
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
            <kbd className="ml-1 hidden rounded border px-1 text-[10px] text-primary-foreground/80 lg:inline" aria-hidden data-testid="approve-shortcut">
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
      </ConfirmDialog>
    </div>
  );
}
