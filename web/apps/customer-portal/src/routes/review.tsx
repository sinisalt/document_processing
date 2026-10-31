import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ExternalLink, Hand, Undo2 } from "lucide-react";
import { Button, Checkbox, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import {
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
  unwrap,
  useApi,
  useAuth,
  useCursorList,
  type ReviewTaskListQuery,
} from "@docproc/app-shell";
import { EMPTY_ARRAY, type ReviewTask } from "@/lib/queries";

export const Route = createFileRoute("/review")({ component: ReviewQueuePage });

const helper = createDataTableColumnHelper<ReviewTask>();
const statusFilters = ["queue", "Open", "InProgress", "Done", "Skipped"] as const;

/** The sidebar hides the queue from roles without Review; a deep link gets an answer instead of a 403 and an empty table. */
function ReviewQueuePage() {
  const { t } = useTranslation();
  const { hasPolicy } = useAuth();
  if (!hasPolicy(Policies.Review)) {
    return <EmptyState title={t("queue.reviewersOnlyTitle")} description={t("queue.reviewersOnly")} />;
  }
  return <ReviewQueue />;
}

function ReviewQueue() {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const { user, hasPolicy } = useAuth();
  const [mine, setMine] = useState(false);
  const [status, setStatus] = useState<(typeof statusFilters)[number]>("queue");

  const query = useMemo<ReviewTaskListQuery>(() => ({ assignedTo: mine ? "me" : undefined, status: status === "queue" ? undefined : status, limit: 50 }), [mine, status]);
  const list = useCursorList<ReviewTask>({
    queryKey: queryKeys.reviewTasks.pages(query),
    queryFn: async (cursor) => unwrap(await client.GET("/api/v1/review-tasks", { params: { query: { ...query, cursor: cursor ?? undefined } } })),
    refetchInterval: 15_000,
  });

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.reviewTasks.all });
  };
  const claim = useMutation({
    mutationFn: async (id: string) => unwrap(await client.POST("/api/v1/review-tasks/{id}:claim", { params: { path: { id } } })),
    onSuccess: () => {
      toast.success(t("queue.claimed"));
      invalidate();
    },
    onSettled: invalidate,
  });
  const release = useMutation({
    mutationFn: async (id: string) => unwrap(await client.POST("/api/v1/review-tasks/{id}:release", { params: { path: { id } } })),
    onSuccess: () => {
      toast.success(t("queue.released"));
      invalidate();
    },
    onSettled: invalidate,
  });

  const me = user?.name;
  const isTenantAdmin = hasPolicy(Policies.TenantAdmin);

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor((task) => task.document?.documentNumber ?? "", {
          id: "documentNumber",
          header: t("inbox.columns.documentNumber"),
          cell: ({ row }) => (
            <Link to="/documents/$documentId" params={{ documentId: row.original.documentId }} className="font-medium underline-offset-2 hover:underline">
              {row.original.document?.documentNumber ?? t("inbox.noNumber")}
            </Link>
          ),
        }),
        helper.accessor((task) => task.document?.supplierName ?? "", { id: "supplier", header: t("inbox.columns.supplier"), cell: (info) => info.getValue() || "—" }),
        helper.accessor((task) => task.document?.totalGross ?? null, {
          id: "total",
          header: t("inbox.columns.total"),
          cell: ({ row }) => <span className="tabular-nums">{formatMoney(row.original.document?.totalGross, row.original.document?.currency, i18n.language)}</span>,
          sortFn: "basic",
        }),
        helper.accessor("reason", { header: t("queue.columns.reason"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        helper.accessor((task) => task.document?.status ?? null, {
          id: "documentStatus",
          header: t("inbox.columns.status"),
          cell: ({ row }) => (row.original.document ? <StatusBadge status={row.original.document.status} /> : "—"),
        }),
        // Task status and assignee share a column so the actions stay on screen at laptop widths.
        helper.accessor("status", {
          header: t("queue.columns.task"),
          cell: ({ row }) => {
            const assignee = row.original.assignedTo;
            return (
              <span className="flex flex-wrap items-center gap-2">
                <ReviewTaskStatusBadge status={row.original.status} />
                {!assignee ? (
                  <span className="text-muted-foreground">{t("queue.unassigned")}</span>
                ) : assignee === me ? (
                  <span className="font-medium">{t("queue.you")}</span>
                ) : (
                  <span>{assignee}</span>
                )}
              </span>
            );
          },
        }),
        helper.accessor("slaDueAt", {
          header: t("queue.columns.slaDue"),
          cell: (info) => {
            const created = t("queue.createdAt", { when: formatDateTime(info.row.original.createdAt, i18n.language) });
            return info.getValue() ? (
              <time dateTime={info.getValue()!} title={created}>
                {formatDateTime(info.getValue(), i18n.language)}
              </time>
            ) : (
              <span title={created}>—</span>
            );
          },
          sortFn: "datetime",
        }),
        helper.display({
          id: "actions",
          header: t("queue.columns.actions"),
          cell: ({ row }) => {
            const task = row.original;
            const open = task.status === "Open" || task.status === "InProgress";
            const mineTask = task.assignedTo === me;
            const canClaim = open && !task.assignedTo;
            const canRelease = open && !!task.assignedTo && (mineTask || isTenantAdmin);
            return (
              <div className="flex items-center gap-1">
                {canClaim && (
                  <Button size="xs" variant="secondary" onClick={() => claim.mutate(task.id)} disabled={claim.isPending} aria-label={t("queue.claimLabel", { number: task.document?.documentNumber ?? task.documentId })}>
                    <Hand aria-hidden />
                    {t("queue.claim")}
                  </Button>
                )}
                {canRelease && (
                  <Button size="xs" variant="outline" onClick={() => release.mutate(task.id)} disabled={release.isPending} aria-label={t("queue.releaseLabel", { number: task.document?.documentNumber ?? task.documentId })}>
                    <Undo2 aria-hidden />
                    {t("queue.release")}
                  </Button>
                )}
                <Button size="xs" variant="ghost" asChild>
                  <Link to="/documents/$documentId" params={{ documentId: task.documentId }} aria-label={t("queue.openLabel", { number: task.document?.documentNumber ?? task.documentId })}>
                    <ExternalLink aria-hidden />
                    {t("queue.open")}
                  </Link>
                </Button>
              </div>
            );
          },
        }),
      ]),
    [t, i18n.language, me, isTenantAdmin, claim, release],
  );

  return (
    <>
      <PageHeader title={t("queue.title")} description={t("queue.description")} />
      <DataTable
        columns={columns}
        data={list.items.length ? list.items : EMPTY_ARRAY}
        getRowId={(row) => row.id}
        isLoading={list.isLoading} query={list}
        caption={t("queue.caption")}
        emptyTitle={t("queue.emptyTitle")}
        emptyDescription={mine ? t("queue.emptyMine") : t("queue.empty")}
        toolbar={
          <div className="flex flex-wrap items-center gap-4">
            <div className="flex items-center gap-2">
              <Checkbox id="queue-mine" checked={mine} onCheckedChange={(checked) => setMine(checked === true)} />
              <Label htmlFor="queue-mine">{t("queue.mine")}</Label>
            </div>
            <div className="flex items-center gap-2">
              <Label htmlFor="queue-status">{t("queue.columns.status")}</Label>
              <Select value={status} onValueChange={(value) => setStatus(value as (typeof statusFilters)[number])}>
                <SelectTrigger id="queue-status" className="w-56" size="sm">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {statusFilters.map((value) => (
                    <SelectItem key={value} value={value}>
                      {value === "queue" ? t("queue.statusQueue") : t(`common:reviewTaskStatus.${value}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>
        }
        footer={
          <>
            <span aria-live="polite">{t("common:table.rowCount", { count: list.items.length })}</span>
            {list.hasNextPage && (
              <Button variant="outline" size="sm" onClick={() => void list.fetchNextPage()} disabled={list.isFetchingNextPage}>
                {t("common:table.loadMore")}
              </Button>
            )}
          </>
        }
      />
    </>
  );
}
