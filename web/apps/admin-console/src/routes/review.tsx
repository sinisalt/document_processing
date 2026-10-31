import { useMemo, useState } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { toast } from "sonner";
import type { components } from "@docproc/api-client";
import { Badge, Button, Checkbox, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import {
  ConfirmDialog,
  DataTable,
  EmptyState,
  PageHeader,
  Policies,
  ReviewTaskStatusBadge,
  StatusBadge,
  createDataTableColumnHelper,
  formatDateTime,
  formatMoney,
  queryKeys,
  relativeTime,
  unwrap,
  useApi,
  useAuth,
  useCursorList,
  type DataTableColumns,
} from "@docproc/app-shell";
import { LoadMore } from "@/components/LoadMore";
import { reviewTaskAction } from "@/lib/review";

type ReviewTask = components["schemas"]["ReviewTaskDto"];
type ReviewTaskStatus = components["schemas"]["ReviewTaskStatus"];

export const reviewTaskStatuses: readonly ReviewTaskStatus[] = ["Open", "InProgress", "Done", "Skipped"];
const OPEN = "__open__";

const searchSchema = z.object({
  status: z.enum(reviewTaskStatuses as readonly [ReviewTaskStatus, ...ReviewTaskStatus[]]).optional().catch(undefined),
  mine: z.boolean().optional().catch(undefined),
});

export const Route = createFileRoute("/review")({ validateSearch: searchSchema, component: ReviewQueuePage });

const helper = createDataTableColumnHelper<ReviewTask>();
const PAGE_SIZE = 50;

/** The global review queue: every tenant's open tasks (platform scope), claim/release, and the way to the document. */
function ReviewQueuePage() {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  const { user, hasPolicy } = useAuth();
  // The whole /review-tasks API needs Review; releasing someone else's task needs TenantAdmin (ReviewTaskEndpoints).
  const canReview = hasPolicy(Policies.Review);
  const canReleaseOthers = hasPolicy(Policies.TenantAdmin);
  const isPlatform = hasPolicy(Policies.Platform);
  const [releasing, setReleasing] = useState<ReviewTask | null>(null);
  const queryClient = useQueryClient();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });

  const query = { status: search.status, assignedTo: search.mine ? "me" : undefined };
  const tasks = useCursorList<ReviewTask>({
    queryKey: queryKeys.reviewTasks.pages(query),
    queryFn: async (cursor) => unwrap(await client.GET("/api/v1/review-tasks", { params: { query: { ...query, limit: PAGE_SIZE, cursor: cursor ?? undefined } } })),
    refetchInterval: 15_000,
    enabled: canReview,
  });

  const claim = useMutation({
    mutationFn: async (task: ReviewTask) => unwrap(await client.POST("/api/v1/review-tasks/{id}:claim", { params: { path: { id: task.id } } })),
    onSuccess: async (updated) => {
      toast.success(t("review.claimed", { document: updated.document?.documentNumber ?? updated.documentId.slice(0, 8) }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.reviewTasks.all });
    },
  });
  const release = useMutation({
    mutationFn: async (task: ReviewTask) => unwrap(await client.POST("/api/v1/review-tasks/{id}:release", { params: { path: { id: task.id } } })),
    onSuccess: async (updated) => {
      toast.success(t("review.released", { document: updated.document?.documentNumber ?? updated.documentId.slice(0, 8) }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.reviewTasks.all });
    },
  });

  // The API names the assignee by `ActorName(user)`: the preferred username, which `/me` returns as `name`.
  const me = user?.name ?? null;

  const columns = useMemo<DataTableColumns<ReviewTask>>(
    () =>
      helper.columns([
        ...(isPlatform
          ? [
              helper.accessor((row) => row.tenantName ?? row.tenantId, {
                id: "tenant",
                header: t("review.tenant"),
                cell: (info) => (info.row.original.tenantName ? <span className="whitespace-nowrap">{info.row.original.tenantName}</span> : <span className="font-mono text-xs">{info.row.original.tenantId.slice(0, 8)}</span>),
              }),
            ]
          : []),
        helper.accessor((row) => row.document?.documentNumber ?? row.documentId, {
          id: "document",
          header: t("review.document"),
          cell: (info) => (
            <Link to="/documents/$documentId" params={{ documentId: info.row.original.documentId }} className="font-medium underline-offset-4 hover:underline">
              {info.row.original.document?.documentNumber ?? <span className="font-mono text-xs">{info.row.original.documentId.slice(0, 8)}</span>}
            </Link>
          ),
        }),
        helper.accessor((row) => row.document?.supplierName ?? "", { id: "supplier", header: t("documents.supplier"), cell: (info) => info.getValue() || "—" }),
        helper.accessor((row) => row.document?.totalGross ?? null, {
          id: "total",
          header: t("documents.total"),
          cell: (info) => <span className="tabular-nums">{formatMoney(info.getValue(), info.row.original.document?.currency, i18n.language)}</span>,
        }),
        helper.accessor("reason", { header: t("review.reason"), cell: (info) => <Badge variant="outline">{info.getValue()}</Badge> }),
        helper.accessor("status", { header: t("review.status"), cell: (info) => <ReviewTaskStatusBadge status={info.getValue()} /> }),
        helper.accessor((row) => row.document?.status ?? "", { id: "documentStatus", header: t("documents.status"), cell: (info) => (info.row.original.document ? <StatusBadge status={info.row.original.document.status} /> : "—") }),
        helper.accessor("assignedTo", { header: t("review.assignedTo"), cell: (info) => (info.getValue() ? <span className={info.getValue() === me ? "font-medium" : undefined}>{info.getValue() === me ? t("review.you") : info.getValue()}</span> : <span className="text-muted-foreground">{t("review.unassigned")}</span>) }),
        helper.accessor("createdAt", { header: t("review.waiting"), sortFn: "datetime", cell: (info) => <span title={formatDateTime(info.getValue(), i18n.language)}>{relativeTime(info.getValue(), i18n.language)}</span> }),
        helper.accessor("slaDueAt", { header: t("review.slaDue"), sortFn: "datetime", cell: (info) => (info.getValue() ? <span className={new Date(info.getValue()!) < new Date() ? "text-destructive" : undefined}>{formatDateTime(info.getValue(), i18n.language)}</span> : "—") }),
        // Claim and release are Review-policy actions; without it there is no column to render.
        ...(canReview
          ? [
              helper.display({
                id: "actions",
                header: () => <span className="sr-only">{t("review.actions")}</span>,
                cell: (info) => {
                  const task = info.row.original;
                  const action = reviewTaskAction(task, me, canReleaseOthers);
                  const label = task.document?.documentNumber ?? task.documentId.slice(0, 8);
                  return (
                    <div className="flex justify-end gap-1">
                      {action === "claim" && (
                        <Button size="sm" disabled={claim.isPending} onClick={() => claim.mutate(task)} aria-label={t("review.claimAria", { document: label })}>
                          {t("review.claim")}
                        </Button>
                      )}
                      {action === "release" && (
                        <Button variant="outline" size="sm" disabled={release.isPending} onClick={() => release.mutate(task)} aria-label={t("review.releaseAria", { document: label })}>
                          {t("review.release")}
                        </Button>
                      )}
                      {action === "releaseOther" && (
                        <Button variant="outline" size="sm" disabled={release.isPending} onClick={() => setReleasing(task)} aria-label={t("review.releaseOtherAria", { document: label, assignee: task.assignedTo })}>
                          {t("review.release")}
                        </Button>
                      )}
                      <Button variant="ghost" size="sm" asChild>
                        <Link to="/documents/$documentId" params={{ documentId: task.documentId }}>
                          {t("review.open")}
                        </Link>
                      </Button>
                    </div>
                  );
                },
              }),
            ]
          : []),
      ]),
    [t, i18n.language, me, claim, release, canReview, canReleaseOthers, isPlatform],
  );

  if (!canReview) {
    return (
      <>
        <PageHeader title={t("review.title")} description={t("review.description")} />
        <EmptyState title={t("review.needsReviewTitle")} description={t("review.needsReviewDescription")} />
      </>
    );
  }

  return (
    <>
      <PageHeader title={t("review.title")} description={t("review.description")} />
      {releasing && (
        <ConfirmDialog
          open
          onOpenChange={(open) => !open && setReleasing(null)}
          title={t("review.releaseOtherTitle", { document: releasing.document?.documentNumber ?? releasing.documentId.slice(0, 8) })}
          description={t("review.releaseOtherDescription", { assignee: releasing.assignedTo })}
          confirmLabel={t("review.release")}
          destructive
          isPending={release.isPending}
          onConfirm={async () => {
            await release.mutateAsync(releasing);
          }}
        />
      )}
      <DataTable
        columns={columns}
        data={tasks.items}
        getRowId={(row) => row.id}
        isLoading={tasks.isLoading} query={tasks}
        caption={t("review.caption")}
        emptyTitle={t("review.empty")}
        emptyDescription={t("review.emptyDescription")}
        toolbar={
          <>
            <Select value={search.status ?? OPEN} onValueChange={(value) => void navigate({ search: (prev) => ({ ...prev, status: value === OPEN ? undefined : (value as ReviewTaskStatus) }), replace: true })}>
              <SelectTrigger className="w-48" aria-label={t("review.status")}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={OPEN}>{t("review.openAndInProgress")}</SelectItem>
                {reviewTaskStatuses.map((status) => (
                  <SelectItem key={status} value={status}>
                    {t(`common:reviewTaskStatus.${status}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <div className="flex items-center gap-2">
              <Checkbox id="review-mine" checked={search.mine === true} onCheckedChange={(checked) => void navigate({ search: (prev) => ({ ...prev, mine: checked === true ? true : undefined }), replace: true })} />
              <Label htmlFor="review-mine">{t("review.mine")}</Label>
            </div>
          </>
        }
        footer={<LoadMore list={tasks} />}
      />
    </>
  );
}
