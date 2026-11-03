import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Building2, Check, ChevronDown, Combine, FileType2, Plus, RotateCcw, Scissors, X } from "lucide-react";
import { Button, Checkbox, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Textarea } from "@docproc/ui";
import { ApiError, ConfirmDialog, canReprocessFrom, defaultReprocessStage, furthestPipelineStatus, isCorrectable, queryKeys, reprocessStages, unwrap, useApi, type ReprocessStage } from "@docproc/app-shell";
import { isApplePlatform } from "@/lib/platform";
import { useDocumentEvents, type DocumentDetail } from "@/lib/queries";
import { AssignSupplierDialog, CreateSupplierDialog, JoinDialog, SetTypeDialog, SplitDialog } from "./ReviewDialogs";

/** Where `:approve` / `:reject` are allowed (docs/05). */
export function canDecide(status: DocumentDetail["status"]): boolean {
  return status === "NeedsReview" || status === "Validated";
}

/** The approval gate's codes (docs/11 §11, `DocumentEndpoints.ApprovalBlockCodes`). */
export const APPROVAL_BLOCK_CODES = ["TYPE_REQUIRED", "NO_DEFINITION", "NO_DATA"] as const;

/** The code of a `409 Cannot approve` from the approval gate, or null for any other error. */
export function approvalRefusalCode(error: unknown): (typeof APPROVAL_BLOCK_CODES)[number] | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null;
  const code = (error.problem as { code?: unknown } | undefined)?.code;
  return typeof code === "string" && (APPROVAL_BLOCK_CODES as readonly string[]).includes(code) ? (code as (typeof APPROVAL_BLOCK_CODES)[number]) : null;
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
  /** The UI language, for type names. */
  locale?: string;
}

/** Which of the review actions' dialogs is open (docs/11 §19 "ReviewActions gains"). */
type ActionDialog = "setType" | "assignSupplier" | "createSupplier" | "split" | "join" | null;

/** Approve, Reject (with a required reason) and Reprocess (from a stage) with their confirmations. */
export function ReviewActions({ document, approveOpen, onApproveOpenChange, hasUnsavedChanges, onDecided, locale = "en" }: ReviewActionsProps) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [dialog, setDialog] = useState<ActionDialog>(null);
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
    // A refusal by the approval gate (docs/11 §11) says why in its code: shown in the UI language, the document reloaded
    // so the reason stays under the button. Any other error goes to the shell's toast.
    onError: (error) => {
      const code = approvalRefusalCode(error);
      if (code === null) {
        if (error instanceof ApiError && error.status === 401) return;
        if (error instanceof ApiError) toast.error(error.title, { description: error.problem?.detail ?? undefined });
        else toast.error(error instanceof Error ? error.message : String(error));
        if (error instanceof ApiError && error.status === 409) void queryClient.invalidateQueries({ refetchType: "active" });
        return;
      }
      toast.error(t("review.actions.approveRefused"), { description: t(`review.actions.approveBlocked.${code}`, { defaultValue: (error as ApiError).problem?.detail ?? code }) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.documents.detail(document.id) });
    },
    meta: { silent: true },
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
  // docs/11 §11: the API refuses to approve a document without a definition or captured data, and says why up front.
  const approvalBlocked = document.approvalBlocked ?? null;
  const blockedReason = approvalBlocked === null ? undefined : t(`review.actions.approveBlocked.${approvalBlocked.code}`, { defaultValue: approvalBlocked.message });
  const apple = isApplePlatform();

  return (
    <div className="flex flex-wrap items-center gap-2" role="group" aria-label={t("review.actions.label")}>
      <ConfirmDialog
        open={approveOpen && approvalBlocked === null}
        onOpenChange={onApproveOpenChange}
        title={t("review.actions.approveTitle")}
        description={t("review.actions.approveDescription", { number: document.documentNumber ?? document.id })}
        confirmLabel={t("review.actions.approve")}
        onConfirm={() => approve.mutateAsync().then(() => undefined)}
        isPending={approve.isPending}
        trigger={
          <Button
            disabled={!decidable || approvalBlocked !== null}
            title={hasUnsavedChanges ? t("review.actions.saveFirst") : blockedReason}
            aria-describedby={approvalBlocked === null ? undefined : "approve-blocked"}
            aria-keyshortcuts={apple ? "Meta+Enter" : "Control+Enter"}
            data-testid="approve-button"
          >
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
      {isCorrectable(document.status) && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" disabled={hasUnsavedChanges} title={hasUnsavedChanges ? t("review.actions.saveFirst") : undefined} data-testid="more-actions">
              {t("review.actions.more")}
              <ChevronDown aria-hidden />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={() => setDialog("setType")} data-testid="action-set-type">
              <FileType2 aria-hidden />
              {t("review.actions.changeType")}
            </DropdownMenuItem>
            <DropdownMenuItem onSelect={() => setDialog("assignSupplier")} data-testid="action-assign-supplier">
              <Building2 aria-hidden />
              {t("review.actions.assignSupplier")}
            </DropdownMenuItem>
            <DropdownMenuItem onSelect={() => setDialog("createSupplier")} data-testid="action-create-supplier">
              <Plus aria-hidden />
              {t("review.actions.createSupplier")}
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => setDialog("split")} disabled={(Number(document.pageCount) || 0) < 2} data-testid="action-split">
              <Scissors aria-hidden />
              {t("review.actions.split")}
            </DropdownMenuItem>
            <DropdownMenuItem onSelect={() => setDialog("join")} data-testid="action-join">
              <Combine aria-hidden />
              {t("review.actions.join")}
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      )}
      <SetTypeDialog document={document} locale={locale} open={dialog === "setType"} onOpenChange={(open) => setDialog(open ? "setType" : null)} />
      <AssignSupplierDialog document={document} open={dialog === "assignSupplier"} onOpenChange={(open) => setDialog(open ? "assignSupplier" : null)} onCreate={() => setDialog("createSupplier")} />
      {dialog === "createSupplier" && <CreateSupplierDialog document={document} open onOpenChange={(open) => setDialog(open ? "createSupplier" : null)} />}
      {dialog === "split" && <SplitDialog document={document} locale={locale} open onOpenChange={(open) => setDialog(open ? "split" : null)} />}
      {dialog === "join" && <JoinDialog document={document} open onOpenChange={(open) => setDialog(open ? "join" : null)} />}
      {approvalBlocked !== null && canDecide(document.status) && (
        <p id="approve-blocked" className="basis-full text-xs text-muted-foreground" role="note" data-testid="approve-blocked" data-code={approvalBlocked.code}>
          {blockedReason}
        </p>
      )}
    </div>
  );
}
