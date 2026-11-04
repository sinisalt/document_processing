import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "@tanstack/react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Archive, Loader2, Play, RefreshCw } from "lucide-react";
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Checkbox, Input, Label, Skeleton, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@docproc/ui";
import { ApiError, ConfirmDialog, EmptyState, Policies, queryKeys, relativeTime, unwrap, useApi, useAuth } from "@docproc/app-shell";
import { useLayoutVersion, useMatchingDocuments, useSupplierLayouts } from "@/lib/layoutQueries";
import { leadVersion, replayRows, warmupLeft, type ReplayReport, type SupplierLayout, type SupplierLayoutVersionSummary } from "@/lib/layouts";
import { useTenantSettings, type Supplier } from "@/lib/queries";

/** The badge variant of a version's status (docs/11 §8.3 lifecycle). */
export function statusVariant(status: string): "success" | "warning" | "secondary" | "destructive" | "outline" {
  switch (status) {
    case "Active":
      return "success";
    case "Candidate":
    case "Shadow":
    case "Draft":
      return "warning";
    case "Degraded":
      return "destructive";
    default:
      return "secondary";
  }
}

/** The replay matrix (docs/11 §8.3): the identification test and the agreement per field over the replay set. */
function ReplayMatrix({ replay }: { replay: ReplayReport }) {
  const { t } = useTranslation();
  const rows = replayRows(replay);
  return (
    <div className="flex flex-col gap-2 text-sm" data-testid="replay-matrix">
      <span className={replay.identificationPassed ? "text-emerald-700 dark:text-emerald-400" : "text-destructive"}>
        {replay.identificationPassed ? t("suppliers.layouts.identificationPassed") : t("suppliers.layouts.identificationFailed", { count: replay.overlaps.length })}
      </span>
      <span className="text-muted-foreground">{t("suppliers.layouts.replaySet", { count: replay.documents.length, threshold: Math.round(Number(replay.threshold) * 100) })}</span>
      {rows.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("suppliers.layouts.field")}</TableHead>
              <TableHead className="text-right">{t("suppliers.layouts.agreement")}</TableHead>
              <TableHead className="text-right">{t("suppliers.layouts.hits")}</TableHead>
              <TableHead className="text-right">{t("suppliers.layouts.mismatches")}</TableHead>
              <TableHead className="text-right">{t("suppliers.layouts.missing")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row) => (
              <TableRow key={row.path}>
                <TableCell className="font-mono text-xs">{row.path}</TableCell>
                <TableCell className={row.hits + row.mismatches > 0 && row.agreement < Math.round(Number(replay.threshold) * 100) ? "text-right text-destructive tabular-nums" : "text-right tabular-nums"}>{row.hits + row.mismatches > 0 ? `${row.agreement} %` : "—"}</TableCell>
                <TableCell className="text-right tabular-nums">{row.hits}</TableCell>
                <TableCell className="text-right tabular-nums">{row.mismatches}</TableCell>
                <TableCell className="text-right tabular-nums">{row.missing}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

/** One version's row with its details: the rule in words, the counters, the warm-up, the replay, the documents waiting for a Candidate. */
function VersionDetails({ layout, version, canActivate }: { layout: SupplierLayout; version: SupplierLayoutVersionSummary; canActivate: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const tenant = useTenantSettings();
  const number = Number(version.version);
  const detail = useLayoutVersion(layout.id, number);
  const candidate = version.status === "Candidate";
  const waiting = useMatchingDocuments(layout.id, number, "pending", candidate);
  const waitingIds = (waiting.data ?? []).filter((d) => d.holds).map((d) => d.documentId);
  const [activating, setActivating] = useState(false);
  const [warmup, setWarmup] = useState("");
  const [reprocessWaiting, setReprocessWaiting] = useState(true);
  const tenantWarmup = Number(tenant.data?.settings?.layoutReviewWarmup ?? 5);
  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.supplierLayouts.all });
    void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
  };

  const replay = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/supplier-layouts/{layoutId}/versions/{version}:replay", { params: { path: { layoutId: layout.id, version: number } }, body: {} })),
    onSuccess: (report) => {
      toast.success(report.passed ? t("suppliers.layouts.replayPassed") : t("suppliers.layouts.replayFailed"));
      invalidate();
    },
  });
  const activate = useMutation({
    mutationFn: async () =>
      unwrap(
        await client.POST("/api/v1/supplier-layouts/{layoutId}/versions/{version}:activate", {
          params: { path: { layoutId: layout.id, version: number } },
          body: { reviewWarmup: warmup.trim() === "" ? null : Number(warmup), reprocessWaiting: waitingIds.length > 0 ? reprocessWaiting : null },
        }),
      ),
    onSuccess: (result) => {
      toast.success(t("suppliers.layouts.activated", { layout: layout.layoutKey, version: number, count: result.reprocessed.length }));
      setActivating(false);
      invalidate();
    },
    onError: (error) => {
      if (error instanceof ApiError) toast.error(t("suppliers.layouts.activateRefused"), { description: error.problem?.detail ?? error.title });
    },
    meta: { silent: true },
  });

  const left = warmupLeft(version);
  const stored = detail.data?.replay as ReplayReport | null | undefined;
  return (
    <div className="flex flex-col gap-3 rounded-md border p-3" data-testid="layout-version" data-version={number} data-status={version.status}>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <span className="font-medium">v{number}</span>
        <Badge variant={statusVariant(version.status)} data-testid="layout-version-status">
          {t(`suppliers.layouts.status.${version.status}`, { defaultValue: version.status })}
        </Badge>
        <Badge variant="outline">{t(`suppliers.layouts.origin.${version.origin}`, { defaultValue: version.origin })}</Badge>
        {version.status === "Active" && (
          <span className="text-muted-foreground" data-testid="layout-warmup">
            {left ? t("suppliers.layouts.warmupLeft", { seen: left.seen, of: left.of }) : Number(version.reviewWarmup) === 0 ? t("suppliers.layouts.noWarmup") : t("suppliers.layouts.warmupDone", { of: Number(version.reviewWarmup) })}
          </span>
        )}
        <span className="ml-auto text-xs text-muted-foreground">{version.createdBy ? t("suppliers.layouts.createdBy", { by: version.createdBy, when: relativeTime(version.createdAt, i18n.language) }) : relativeTime(version.createdAt, i18n.language)}</span>
      </div>
      <p className="text-sm" data-testid="layout-rule">
        {version.describe}
      </p>
      <div className="flex flex-wrap gap-4 text-xs text-muted-foreground">
        <span>{t("suppliers.layouts.stats.identified", { count: Number(version.identified) })}</span>
        <span>{t("suppliers.layouts.stats.applied", { count: Number(version.applied) })}</span>
        <span>{t("suppliers.layouts.stats.corrected", { count: Number(version.corrected) })}</span>
        {Number(version.consecutiveFailures) > 0 && <span className="text-destructive">{t("suppliers.layouts.stats.failures", { count: Number(version.consecutiveFailures) })}</span>}
      </div>
      {stored && <ReplayMatrix replay={stored} />}
      {candidate && (
        <div className="flex flex-col gap-1 text-sm" data-testid="layout-waiting">
          <span className="font-medium">{t("suppliers.layouts.waiting", { count: waitingIds.length })}</span>
          {waitingIds.length > 0 && (
            <ul className="flex flex-wrap gap-2">
              {(waiting.data ?? [])
                .filter((d) => d.holds)
                .map((d) => (
                  <li key={d.documentId}>
                    <Link to="/documents/$documentId" params={{ documentId: d.documentId }} className="text-xs underline-offset-2 hover:underline">
                      {d.fileName ?? d.documentId.slice(0, 8)}
                    </Link>
                  </li>
                ))}
            </ul>
          )}
        </div>
      )}
      {canActivate && (version.status === "Candidate" || version.status === "Active" || version.status === "Degraded") && (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="outline" onClick={() => replay.mutate()} disabled={replay.isPending} data-testid="layout-replay">
            {replay.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <RefreshCw aria-hidden />}
            {t("suppliers.layouts.replay")}
          </Button>
          {version.status !== "Active" && (
            <Button size="sm" onClick={() => setActivating(true)} data-testid="layout-activate">
              <Play aria-hidden />
              {waitingIds.length > 0 ? t("suppliers.layouts.activateAndReprocess", { count: waitingIds.length }) : t("suppliers.layouts.activate")}
            </Button>
          )}
        </div>
      )}
      <ConfirmDialog
        open={activating}
        onOpenChange={setActivating}
        title={t("suppliers.layouts.activateTitle", { layout: layout.layoutKey, version: number })}
        description={t("suppliers.layouts.activateDescription")}
        confirmLabel={t("suppliers.layouts.activate")}
        onConfirm={() => activate.mutateAsync().then(() => undefined)}
        isPending={activate.isPending}
      >
        <div className="flex flex-col gap-3">
          <div className="flex flex-col gap-1">
            <Label htmlFor="activate-warmup">{t("suppliers.layouts.warmupField")}</Label>
            <Input id="activate-warmup" type="number" min={0} max={100} value={warmup} placeholder={String(tenantWarmup)} onChange={(e) => setWarmup(e.target.value)} className="w-24" data-testid="activate-warmup" />
            <span className="text-xs text-muted-foreground">{t("suppliers.layouts.warmupFieldHint", { value: tenantWarmup })}</span>
          </div>
          {waitingIds.length > 0 && (
            <div className="flex items-center gap-2">
              <Checkbox id="activate-reprocess" checked={reprocessWaiting} onCheckedChange={(c) => setReprocessWaiting(c === true)} />
              <Label htmlFor="activate-reprocess" className="font-normal">
                {t("suppliers.layouts.reprocessWaiting", { count: waitingIds.length })}
              </Label>
            </div>
          )}
        </div>
      </ConfirmDialog>
    </div>
  );
}

/**
 * The Suppliers sheet's Layouts tab (docs/11 §19 item 3, §8): the supplier's layouts grouped by type, one card per layout
 * with its versions: status, origin, the rule in words, the counters, the warm-up, the replay matrix; a Candidate lists
 * the documents waiting for it and offers "Activate and reprocess N documents" to a MappingAuthor. Retire a layout.
 */
export function SupplierLayoutsTab({ supplier }: { supplier: Supplier }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const { hasPolicy } = useAuth();
  const canActivate = hasPolicy(Policies.MappingAuthor);
  const layouts = useSupplierLayouts(supplier.id);
  const [retiring, setRetiring] = useState<SupplierLayout | null>(null);
  const retire = useMutation({
    mutationFn: async (layout: SupplierLayout) => unwrap(await client.POST("/api/v1/supplier-layouts/{layoutId}:retire", { params: { path: { layoutId: layout.id } } })),
    onSuccess: (layout) => {
      toast.success(t("suppliers.layouts.retired", { layout: layout.layoutKey }));
      void queryClient.invalidateQueries({ queryKey: queryKeys.supplierLayouts.all });
    },
  });

  if (layouts.isPending) return <Skeleton className="h-32" />;
  const list = layouts.data ?? [];
  if (list.length === 0) return <EmptyState title={t("suppliers.layouts.empty")} description={t("suppliers.layouts.emptyDescription")} />;
  const byType = new Map<string, SupplierLayout[]>();
  for (const layout of list) byType.set(layout.documentTypeKey, [...(byType.get(layout.documentTypeKey) ?? []), layout]);

  return (
    <div className="flex flex-col gap-6" data-testid="supplier-layouts">
      {[...byType.entries()].map(([type, group]) => (
        <section key={type} className="flex flex-col gap-3">
          <h3 className="text-sm font-semibold">{t("suppliers.layouts.typeHeading", { type: t(`documentType.${type}`, { defaultValue: type }), layouts: group.map((l) => l.layoutKey).join(", ") })}</h3>
          {group.map((layout) => {
            const lead = leadVersion(layout);
            return (
              <Card key={layout.id} data-testid="layout-card" data-layout-key={layout.layoutKey}>
                <CardHeader className="gap-1">
                  <CardTitle className="flex flex-wrap items-center gap-2 text-base">
                    <span className="font-mono">{layout.layoutKey}</span>
                    <Badge variant={statusVariant(layout.status)}>{t(`suppliers.layouts.status.${layout.status}`, { defaultValue: layout.status })}</Badge>
                    {lead && <span className="text-xs font-normal text-muted-foreground">{t("suppliers.layouts.priority", { priority: Number(layout.priority) })}</span>}
                  </CardTitle>
                  <CardDescription>
                    {layout.name}
                    {layout.lastMatchedAt && ` · ${t("suppliers.layouts.lastMatched", { when: relativeTime(layout.lastMatchedAt, i18n.language) })}`}
                  </CardDescription>
                </CardHeader>
                <CardContent className="flex flex-col gap-3">
                  {layout.versions.map((version) => (
                    <VersionDetails key={version.id} layout={layout} version={version} canActivate={canActivate} />
                  ))}
                  {canActivate && layout.status !== "Retired" && (
                    <div>
                      <Button size="sm" variant="ghost" onClick={() => setRetiring(layout)} data-testid="layout-retire">
                        <Archive aria-hidden />
                        {t("suppliers.layouts.retire")}
                      </Button>
                    </div>
                  )}
                </CardContent>
              </Card>
            );
          })}
        </section>
      ))}
      <ConfirmDialog
        open={retiring !== null}
        onOpenChange={(open) => !open && setRetiring(null)}
        title={t("suppliers.layouts.retireTitle", { layout: retiring?.layoutKey ?? "" })}
        description={t("suppliers.layouts.retireDescription")}
        confirmLabel={t("suppliers.layouts.retire")}
        destructive
        onConfirm={() => (retiring ? retire.mutateAsync(retiring).then(() => setRetiring(null)) : undefined)}
      />
    </div>
  );
}
