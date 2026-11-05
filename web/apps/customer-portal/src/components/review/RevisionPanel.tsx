import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { CopyX, Files, Loader2, Replace, Unplug } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Button, Label, Skeleton, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, Textarea } from "@docproc/ui";
import { formatDate, formatDateTime, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { useDocument, type DocumentDetail } from "@/lib/queries";

type Resolution = "supersedes" | "additional" | "duplicate";

function useSettle(documentId: string) {
  const queryClient = useQueryClient();
  return (updated?: DocumentDetail) => {
    if (updated) queryClient.setQueryData(queryKeys.documents.detail(documentId), updated);
    void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
    void queryClient.invalidateQueries({ queryKey: queryKeys.reviewTasks.all });
  };
}

/** The open flag of a code on a document, or null. */
export function openFlag(document: Pick<DocumentDetail, "flags">, code: string) {
  return document.flags.find((flag) => flag.code === code && !flag.resolvedAt) ?? null;
}

/**
 * `REVISION_OF` (docs/11 §11): an earlier document with the same duplicate key exists. The reviewer says what this one
 * is to it: it replaces it (the earlier one is superseded), it is an additional document, or a duplicate (rejected).
 */
export function RevisionPanel({ document, canReview }: { document: DocumentDetail; canReview: boolean }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const settle = useSettle(document.id);
  const [note, setNote] = useState("");
  const flag = openFlag(document, "REVISION_OF");
  const earlier = flag?.params.documentId;
  const resolve = useMutation({
    mutationFn: async (resolution: Resolution) =>
      unwrap(await client.POST("/api/v1/documents/{id}:resolve-revision", { params: { path: { id: document.id } }, body: { resolution, note: note.trim() || null } })),
    onSuccess: (updated, resolution) => {
      toast.success(t(`review.revision.done.${resolution}`));
      settle(updated);
    },
  });
  if (!flag) return null;
  return (
    <Alert data-testid="revision-panel">
      <Files aria-hidden />
      <AlertTitle>{t("review.revision.title")}</AlertTitle>
      <AlertDescription className="flex flex-col gap-3">
        <p>
          {t("review.revision.description")}{" "}
          {earlier && (
            <a className="underline underline-offset-2" href={`/documents/${earlier}`}>
              {t("review.revision.open")}
            </a>
          )}
        </p>
        {earlier && <RevisionComparison document={document} earlierId={earlier} />}
        {canReview && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="revision-note">{t("review.revision.note")}</Label>
            <Textarea id="revision-note" value={note} maxLength={2000} onChange={(event) => setNote(event.target.value)} data-testid="revision-note" />
          </div>
        )}
        {canReview && (
          <div className="flex flex-wrap gap-2">
            {(
              [
                ["supersedes", Replace],
                ["additional", Files],
                ["duplicate", CopyX],
              ] as const
            ).map(([resolution, Icon]) => (
              <Button
                key={resolution}
                type="button"
                size="sm"
                variant={resolution === "duplicate" ? "destructive" : "secondary"}
                disabled={resolve.isPending}
                onClick={() => resolve.mutate(resolution)}
                data-testid={`revision-${resolution}`}
              >
                {resolve.isPending && resolve.variables === resolution ? <Loader2 className="animate-spin" aria-hidden /> : <Icon aria-hidden />}
                {t(`review.revision.${resolution}`)}
              </Button>
            ))}
          </div>
        )}
      </AlertDescription>
    </Alert>
  );
}

/** The facts of a document the comparison shows, in order. */
export function revisionFacts(document: Pick<DocumentDetail, "documentNumber" | "status" | "createdAt" | "approvedAt" | "validFrom" | "validUntil" | "lineCount">) {
  return [
    ["number", document.documentNumber ?? null],
    ["validFrom", document.validFrom ?? null],
    ["validUntil", document.validUntil ?? null],
    ["lineCount", document.lineCount === null || document.lineCount === undefined ? null : String(document.lineCount)],
    ["status", document.status],
    ["received", document.createdAt],
    ["approved", document.approvedAt ?? null],
  ] as const;
}

/**
 * The earlier document side by side with this one (docs/11 §19 item 1, "A REVISION_OF panel showing the earlier
 * document side by side"): number, validity, row count, status and dates; a row whose values differ is marked.
 */
function RevisionComparison({ document, earlierId }: { document: DocumentDetail; earlierId: string }) {
  const { t, i18n } = useTranslation();
  const earlier = useDocument(earlierId);
  if (earlier.isPending) return <Skeleton className="h-24 w-full" />;
  if (!earlier.data) return null;
  const mine = revisionFacts(document);
  const theirs = revisionFacts(earlier.data);
  const show = (key: string, value: string | null) => {
    if (value === null) return "—";
    if (key === "received" || key === "approved") return formatDateTime(value, i18n.language);
    if (key === "validFrom" || key === "validUntil") return formatDate(value, i18n.language);
    if (key === "status") return t(`common:documentStatus.${value}`, { defaultValue: value });
    return value;
  };
  return (
    <Table data-testid="revision-comparison">
      <TableHeader>
        <TableRow>
          <TableHead />
          <TableHead>{t("review.revision.earlier")}</TableHead>
          <TableHead>{t("review.revision.this")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {mine.map(([key, value], index) => {
          const other = theirs[index]![1];
          const differs = key !== "status" && key !== "received" && key !== "approved" && value !== other;
          return (
            <TableRow key={key} data-fact={key} data-differs={differs}>
              <TableCell className="text-xs text-muted-foreground">{t(`review.revision.facts.${key}`)}</TableCell>
              <TableCell className="text-xs">{show(key, other)}</TableCell>
              <TableCell className={differs ? "text-xs font-medium text-amber-700 dark:text-amber-400" : "text-xs"}>{show(key, value)}</TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

/**
 * `EXTERNAL_REJECTED` (docs/11 §13.8): the receiving system rejected the delivered document; it stays settled. The
 * reviewer corrects it (reprocess and approve again) or resolves the issue with a note.
 */
export function IntegrationIssuePanel({ document, canReview }: { document: DocumentDetail; canReview: boolean }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const settle = useSettle(document.id);
  const [note, setNote] = useState("");
  const flag = openFlag(document, "EXTERNAL_REJECTED");
  const resolve = useMutation({
    mutationFn: async () =>
      unwrap(await client.POST("/api/v1/documents/{id}:resolve-integration-issue", { params: { path: { id: document.id } }, body: { note } })),
    onSuccess: (updated) => {
      toast.success(t("review.integrationIssue.done"));
      setNote("");
      settle(updated);
    },
  });
  if (!flag) return null;
  return (
    <Alert variant="destructive" data-testid="integration-issue-panel">
      <Unplug aria-hidden />
      <AlertTitle>{t("review.integrationIssue.title", { destination: flag.params.destination ?? "" })}</AlertTitle>
      <AlertDescription className="flex flex-col gap-3">
        <p>{flag.params.message ? t("review.integrationIssue.message", { message: flag.params.message }) : t("review.integrationIssue.noMessage")}</p>
        <p>{t("review.integrationIssue.description")}</p>
        {canReview && (
          <div className="flex flex-col gap-2">
            <Label htmlFor="integration-issue-note">{t("review.integrationIssue.note")}</Label>
            <Textarea id="integration-issue-note" value={note} maxLength={2000} onChange={(event) => setNote(event.target.value)} data-testid="integration-issue-note" />
            <div>
              <Button type="button" size="sm" variant="secondary" disabled={note.trim().length === 0 || resolve.isPending} onClick={() => resolve.mutate()} data-testid="integration-issue-resolve">
                {resolve.isPending && <Loader2 className="animate-spin" aria-hidden />}
                {t("review.integrationIssue.resolve")}
              </Button>
            </div>
          </div>
        )}
      </AlertDescription>
    </Alert>
  );
}
