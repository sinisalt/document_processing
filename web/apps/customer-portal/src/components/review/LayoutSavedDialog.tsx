import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, RotateCcw } from "lucide-react";
import { Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Skeleton } from "@docproc/ui";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { useLayoutVersion, useMatchingDocuments, useSupplierLayouts } from "@/lib/layoutQueries";
import { replayRows, warmupLeft } from "@/lib/layouts";
import type { ReviewSession } from "@/lib/reviewSession";

type Saved = NonNullable<ReviewSession["savedLayout"]>;

/**
 * After "Approve and save as layout" (docs/11 §12.10 steps 6 and 7): which layout and version the approval saved and
 * its state (Active with its warm-up, or a Candidate waiting for a MappingAuthor), the replay that decided it, and the
 * siblings: other documents in review the new rule holds on, reprocessed from Classify on request when it is Active
 * (attached to the Candidate otherwise).
 */
export function LayoutSavedDialog({ saved, documentId, onClose }: { saved: Saved; documentId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const layouts = useSupplierLayouts(saved.supplierId);
  const layout = (layouts.data ?? []).find((l) => (saved.pendingLayoutVersionId ? l.versions.some((v) => v.id === saved.pendingLayoutVersionId) : l.layoutKey === saved.layoutKey && l.documentTypeKey === saved.typeKey)) ?? null;
  const version = layout ? (layout.versions.find((v) => v.id === saved.pendingLayoutVersionId) ?? layout.versions[0] ?? null) : null;
  const detail = useLayoutVersion(layout?.id, version ? Number(version.version) : null);
  const siblings = useMatchingDocuments(layout?.id, version ? Number(version.version) : null, "pending");
  const others = (siblings.data ?? []).filter((d) => d.documentId !== documentId && d.holds);
  const active = version?.status === "Active";
  const replay = detail.data?.replay as Parameters<typeof replayRows>[0] | undefined;
  const rows = replayRows(replay);
  const warmup = version ? warmupLeft(version) : null;

  const reprocess = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/documents:bulk-reprocess", { body: { documentIds: others.map((d) => d.documentId), fromStage: "Classify", configMode: "current" } })),
    onSuccess: (items) => {
      toast.success(t("review.layoutSaved.reprocessed", { count: items.filter((i) => i.queued).length }));
      void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-lg" data-testid="layout-saved-dialog">
        <DialogHeader>
          <DialogTitle>{t("review.layoutSaved.title")}</DialogTitle>
          <DialogDescription>{layout ? t("review.layoutSaved.description", { layout: layout.layoutKey, version: version?.version ?? "" }) : t("review.layoutSaved.loading")}</DialogDescription>
        </DialogHeader>
        {layouts.isPending ? (
          <Skeleton className="h-16" />
        ) : version ? (
          <div className="flex flex-col gap-3 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant={active ? "success" : "warning"} data-testid="layout-saved-status" data-status={version.status}>
                {t(`suppliers.layouts.status.${version.status}`, { defaultValue: version.status })}
              </Badge>
              <span>{active ? (warmup ? t("review.layoutSaved.warmup", { count: warmup.of }) : t("review.layoutSaved.noWarmup")) : t("review.layoutSaved.candidate")}</span>
            </div>
            <p className="text-muted-foreground">{version.describe}</p>
            {replay && (
              <div className="flex flex-col gap-1" data-testid="layout-saved-replay">
                <span className="font-medium">{replay.identificationPassed ? t("review.layoutSaved.identificationPassed") : t("review.layoutSaved.identificationFailed")}</span>
                <span className="text-muted-foreground">{t("review.layoutSaved.replayFields", { count: rows.length, documents: replay.documents.length, lowest: rows[0]?.agreement ?? 100 })}</span>
              </div>
            )}
            {others.length > 0 && (
              <p data-testid="layout-saved-siblings">{active ? t("review.layoutSaved.siblingsActive", { count: others.length }) : t("review.layoutSaved.siblingsCandidate", { count: others.length })}</p>
            )}
          </div>
        ) : (
          <p className="text-sm text-muted-foreground">{t("review.layoutSaved.notFound")}</p>
        )}
        <DialogFooter>
          {active && others.length > 0 && (
            <Button type="button" onClick={() => reprocess.mutate()} disabled={reprocess.isPending} data-testid="layout-saved-reprocess">
              {reprocess.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <RotateCcw aria-hidden />}
              {t("review.layoutSaved.reprocess", { count: others.length })}
            </Button>
          )}
          <Button type="button" variant="outline" onClick={onClose} data-testid="layout-saved-close">
            {t("common:actions.close")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
