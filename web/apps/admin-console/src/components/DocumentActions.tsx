import { useState, type ReactNode } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import { Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Textarea } from "@docproc/ui";
import { ConfirmDialog, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { defaultReprocessStage, reprocessStages, type ReprocessStage } from "@/lib/documents";

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

/** `POST /documents/{id}:reprocess` with a stage picker; a 409 (wrong state, stage never reached) is toasted by the shell. */
export function ReprocessDialog({ documentId, documentLabel, open, onOpenChange, trigger }: ActionDialogProps) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const invalidate = useDocumentInvalidation();
  const [stage, setStage] = useState<ReprocessStage>(defaultReprocessStage);
  const mutation = useMutation({
    mutationFn: async (fromStage: ReprocessStage) => unwrap(await client.POST("/api/v1/documents/{id}:reprocess", { params: { path: { id: documentId } }, body: { fromStage } })),
    onSuccess: async () => {
      toast.success(t("document.reprocessQueued", { document: documentLabel, stage: t(`stages.${stage}`, { defaultValue: stage }) }));
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
      onConfirm={async () => { await mutation.mutateAsync(stage); }}
    >
      <div className="grid gap-2">
        <Label htmlFor={`reprocess-stage-${documentId}`}>{t("document.fromStage")}</Label>
        <Select value={stage} onValueChange={(value) => setStage(value as ReprocessStage)}>
          <SelectTrigger id={`reprocess-stage-${documentId}`} className="w-full" aria-label={t("document.fromStage")}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {reprocessStages.map((s) => (
              <SelectItem key={s} value={s}>
                {t(`stages.${s}`, { defaultValue: s })}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <p className="text-xs text-muted-foreground">{t("document.fromStageHint")}</p>
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
