import { useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import { RotateCcw } from "lucide-react";
import { Button, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Textarea } from "@docproc/ui";
import { ConfirmDialog, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { canReprocessFrom, defaultReprocessStage, furthestPipelineStatus, reprocessStages, type DocumentStatus, type ReprocessStage } from "@/lib/documents";
import type { DocumentEvent } from "@/lib/timeline";

const NO_EVENTS: readonly DocumentEvent[] = [];

interface ActionDialogProps {
  documentId: string;
  /** A short name for the toast (document number or id). */
  documentLabel: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  trigger?: ReactNode;
}

/** Invalidates everything a document decision changes: the document itself, every list, and the review queue. */
function useDocumentInvalidation() {
  const queryClient = useQueryClient();
  return async () => {
    await Promise.all([queryClient.invalidateQueries({ queryKey: queryKeys.documents.all }), queryClient.invalidateQueries({ queryKey: queryKeys.reviewTasks.all })]);
  };
}

interface ReprocessDialogProps extends ActionDialogProps {
  /** The document's current status. */
  status?: DocumentStatus;
  /**
   * The document's timeline, when the caller has it (the detail page). Without it the dialog loads it while open:
   * which stages are offered depends on how far the document got.
   */
  events?: readonly DocumentEvent[];
}

/**
 * `POST /documents/{id}:reprocess` with a stage picker. It defaults to the latest stage the document can rerun from
 * and disables the later ones (the API answers those with 409 "Stage not reached"); other 409s are toasted by the shell.
 */
export function ReprocessDialog({ documentId, documentLabel, open, onOpenChange, trigger, status, events }: ReprocessDialogProps) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const invalidate = useDocumentInvalidation();
  const loaded = useQuery({
    queryKey: queryKeys.documents.events(documentId),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/events", { params: { path: { id: documentId } } })),
    enabled: open && events === undefined,
  });
  // A timeline that did not load still leaves what the current status proves (Normalize at least), with the API's 409
  // as the safety net, instead of a dialog stuck on "Loading the timeline".
  const timelineFailed = events === undefined && loaded.isError;
  const timeline = events ?? loaded.data ?? (timelineFailed ? NO_EVENTS : undefined);
  const furthest = timeline ? furthestPipelineStatus(timeline, status) : null;
  const [picked, setPicked] = useState<ReprocessStage | null>(null);
  const stage: ReprocessStage | null = furthest === null ? null : picked !== null && canReprocessFrom(picked, furthest) ? picked : defaultReprocessStage(furthest);
  const mutation = useMutation({
    mutationFn: async (fromStage: ReprocessStage) => unwrap(await client.POST("/api/v1/documents/{id}:reprocess", { params: { path: { id: documentId } }, body: { fromStage } })),
    onSuccess: async (_document, fromStage) => {
      toast.success(t("document.reprocessQueued", { document: documentLabel, stage: t(`stages.${fromStage}`, { defaultValue: fromStage }) }));
      await invalidate();
    },
  });

  return (
    <ConfirmDialog
      open={open}
      onOpenChange={onOpenChange}
      trigger={trigger}
      title={t("document.reprocessTitle", { document: documentLabel })}
      description={t("document.reprocessDescription")}
      confirmLabel={t("document.reprocess")}
      isPending={mutation.isPending}
      confirmDisabled={stage === null}
      onConfirm={async () => {
        if (stage !== null) await mutation.mutateAsync(stage);
      }}
    >
      <div className="grid gap-2">
        <Label htmlFor={`reprocess-stage-${documentId}`}>{t("document.fromStage")}</Label>
        <Select value={stage ?? ""} onValueChange={(value) => setPicked(value as ReprocessStage)} disabled={stage === null}>
          <SelectTrigger id={`reprocess-stage-${documentId}`} className="w-full" aria-label={t("document.fromStage")}>
            <SelectValue placeholder={t("document.fromStageLoading")} />
          </SelectTrigger>
          <SelectContent>
            {reprocessStages.map((s) => (
              <SelectItem key={s} value={s} disabled={furthest === null || !canReprocessFrom(s, furthest)}>
                {t(`stages.${s}`, { defaultValue: s })}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <p className="text-xs text-muted-foreground">{t("document.fromStageHint")}</p>
        {timelineFailed && (
          <div role="alert" className="flex flex-wrap items-center gap-2 text-xs text-destructive" data-testid="reprocess-timeline-failed">
            <span className="flex-1">{t("document.fromStageTimelineFailed")}</span>
            <Button type="button" variant="outline" size="sm" onClick={() => void loaded.refetch()} disabled={loaded.isFetching}>
              <RotateCcw aria-hidden />
              {t("common:errors.tryAgain")}
            </Button>
          </div>
        )}
        {furthest !== null && !canReprocessFrom("Validate", furthest) && (
          <p className="text-xs text-muted-foreground" data-testid="reprocess-reached">
            {t("document.fromStageReached", { status: t(`common:documentStatus.${furthest}`, { defaultValue: furthest }) })}
          </p>
        )}
      </div>
    </ConfirmDialog>
  );
}

/** `POST /documents/{id}:reject` with the mandatory reason. */
export function RejectDialog({ documentId, documentLabel, open, onOpenChange, trigger }: ActionDialogProps) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const invalidate = useDocumentInvalidation();
  const [reason, setReason] = useState("");
  const mutation = useMutation({
    mutationFn: async (value: string) => unwrap(await client.POST("/api/v1/documents/{id}:reject", { params: { path: { id: documentId } }, body: { reason: value.trim() } })),
    onSuccess: async () => {
      toast.success(t("document.rejected", { document: documentLabel }));
      setReason("");
      await invalidate();
    },
  });

  return (
    <ConfirmDialog
      open={open}
      onOpenChange={onOpenChange}
      trigger={trigger}
      destructive
      title={t("document.rejectTitle", { document: documentLabel })}
      description={t("document.rejectDescription")}
      confirmLabel={t("document.reject")}
      isPending={mutation.isPending}
      confirmDisabled={reason.trim().length === 0}
      onConfirm={async () => { await mutation.mutateAsync(reason); }}
    >
      <div className="grid gap-2">
        <Label htmlFor={`reject-reason-${documentId}`}>{t("document.rejectReason")}</Label>
        <Textarea id={`reject-reason-${documentId}`} value={reason} onChange={(event) => setReason(event.target.value)} rows={3} required />
      </div>
    </ConfirmDialog>
  );
}

/** `POST /documents/{id}:approve` with an optional comment; only offered to callers with the Review policy. */
export function ApproveDialog({ documentId, documentLabel, open, onOpenChange, trigger }: ActionDialogProps) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const invalidate = useDocumentInvalidation();
  const [comment, setComment] = useState("");
  const mutation = useMutation({
    mutationFn: async (value: string) =>
      unwrap(await client.POST("/api/v1/documents/{id}:approve", { params: { path: { id: documentId } }, body: { comment: value.trim() === "" ? null : value.trim() } })),
    onSuccess: async () => {
      toast.success(t("document.approved", { document: documentLabel }));
      setComment("");
      await invalidate();
    },
  });

  return (
    <ConfirmDialog
      open={open}
      onOpenChange={onOpenChange}
      trigger={trigger}
      title={t("document.approveTitle", { document: documentLabel })}
      description={t("document.approveDescription")}
      confirmLabel={t("document.approve")}
      isPending={mutation.isPending}
      onConfirm={async () => { await mutation.mutateAsync(comment); }}
    >
      <div className="grid gap-2">
        <Label htmlFor={`approve-comment-${documentId}`}>{t("document.approveComment")}</Label>
        <Textarea id={`approve-comment-${documentId}`} value={comment} onChange={(event) => setComment(event.target.value)} rows={2} />
      </div>
    </ConfirmDialog>
  );
}
