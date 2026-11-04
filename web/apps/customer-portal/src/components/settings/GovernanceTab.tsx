import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Download, FlaskConical, Loader2, Trash2, Upload } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Checkbox, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, Textarea } from "@docproc/ui";
import { ApiError, ConfirmDialog, EmptyState, formatDateTime, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { useSupplierLayouts } from "@/lib/layoutQueries";
import { useSuppliers } from "@/lib/queries";

type BundleImport = components["schemas"]["BundleImportResponse"];
type Run = components["schemas"]["RegressionRunDto"];

export const BUNDLE_PARTS = ["types", "layouts", "sourceRules", "outputs", "bindings"] as const;

/** A file name for an exported bundle: `docproc-config-<slug>-<yyyy-mm-dd>.json`. */
export function bundleFileName(tenantSlug: string | null | undefined, now: Date = new Date()): string {
  return `docproc-config-${(tenantSlug ?? "tenant").replace(/[^a-z0-9-]+/gi, "-").toLowerCase()}-${now.toISOString().slice(0, 10)}.json`;
}

/** A run's result in short: passed, and the documents or values that differ. */
function RunResult({ run }: { run: Run }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-2 text-sm" data-testid="governance-run-result">
      <Badge variant={run.passed ? "success" : "destructive"}>{run.passed ? t("settings.governance.passed") : t("settings.governance.failed")}</Badge>
      <pre className="max-h-64 overflow-auto rounded-md bg-muted p-2 font-mono text-xs">{JSON.stringify(run.result, null, 2)}</pre>
    </div>
  );
}

/**
 * Settings > Governance (docs/11 §19 item 6, §15.3, §15.4): golden documents (approved documents whose values are kept as
 * the expected output), sandbox runs of a document through a layout version or a draft rule and mapping (nothing is
 * written), regression runs of a version against the golden documents, and the configuration bundle: export as signed
 * JSON (no secrets) and import with a dry run first.
 */
export function GovernanceTab() {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const golden = useQuery({ queryKey: queryKeys.governance.golden(), queryFn: async () => unwrap(await client.GET("/api/v1/golden-documents")) });
  const [removing, setRemoving] = useState<string | null>(null);
  const remove = useMutation({
    mutationFn: async (id: string) => unwrap(await client.DELETE("/api/v1/golden-documents/{id}", { params: { path: { id } } })),
    onSuccess: () => {
      toast.success(t("settings.governance.goldenRemoved"));
      void queryClient.invalidateQueries({ queryKey: queryKeys.governance.all });
    },
  });

  // Sandbox and regression runs pick a supplier's layout version.
  const suppliers = useSuppliers({ limit: 200 });
  const [supplierId, setSupplierId] = useState("");
  const layouts = useSupplierLayouts(supplierId || null);
  const versions = useMemo(() => (layouts.data ?? []).flatMap((l) => l.versions.map((v) => ({ layout: l, version: v }))), [layouts.data]);
  const [versionId, setVersionId] = useState("");
  const chosen = versions.find((v) => v.version.id === versionId);
  const [documentId, setDocumentId] = useState("");
  const [draftRule, setDraftRule] = useState("");
  const [draftMapping, setDraftMapping] = useState("");
  const [run, setRun] = useState<Run | null>(null);

  const sandbox = useMutation({
    mutationFn: async () => {
      const draft = draftRule.trim() !== "" || draftMapping.trim() !== "";
      const body = draft
        ? { documentId: documentId.trim(), identification: JSON.parse(draftRule || "null") as never, mapping: JSON.parse(draftMapping || "null") as never, typeKey: chosen?.layout.documentTypeKey ?? null }
        : { documentId: documentId.trim(), layoutVersionId: versionId };
      return unwrap(await client.POST("/api/v1/sandbox/runs", { body }));
    },
    onSuccess: setRun,
    onError: (error) => toast.error(error instanceof ApiError ? error.title : t("settings.governance.jsonInvalid"), { description: error instanceof ApiError ? (error.problem?.detail ?? undefined) : error.message }),
    meta: { silent: true },
  });
  const regression = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/regression-runs", { body: { layoutId: chosen!.layout.id, version: Number(chosen!.version.version) } })),
    onSuccess: setRun,
  });

  // Bundles.
  const [include, setInclude] = useState<string[]>([...BUNDLE_PARTS]);
  const exportBundle = useMutation({
    mutationFn: async () => unwrap(await client.GET("/api/v1/config/bundle", { params: { query: { include: include.join(",") } } })),
    onSuccess: (bundle) => {
      const blob = new Blob([JSON.stringify(bundle, null, 2)], { type: "application/json" });
      const url = URL.createObjectURL(blob);
      const anchor = window.document.createElement("a");
      anchor.href = url;
      anchor.download = bundleFileName((bundle as { sourceTenantSlug?: string }).sourceTenantSlug);
      anchor.click();
      URL.revokeObjectURL(url);
      toast.success(t("settings.governance.exported"));
    },
  });
  const [bundleText, setBundleText] = useState("");
  const [imported, setImported] = useState<BundleImport | null>(null);
  const importBundle = useMutation({
    mutationFn: async (mode: "dry-run" | "apply") => unwrap(await client.POST("/api/v1/config/bundle:import", { body: { bundle: JSON.parse(bundleText) as never, mode } })),
    onSuccess: (result) => {
      setImported(result);
      if (result.mode === "apply") toast.success(t("settings.governance.importApplied"));
    },
    onError: (error) => toast.error(error instanceof ApiError ? error.title : t("settings.governance.jsonInvalid"), { description: error instanceof ApiError ? (error.problem?.detail ?? undefined) : error.message }),
    meta: { silent: true },
  });

  return (
    <div className="grid grid-cols-1 items-start gap-6 xl:grid-cols-2" data-testid="governance-tab">
      <Card className="xl:col-span-2">
        <CardHeader>
          <CardTitle>{t("settings.governance.goldenTitle")}</CardTitle>
          <CardDescription>{t("settings.governance.goldenDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          {(golden.data ?? []).length === 0 ? (
            <EmptyState title={t("settings.governance.goldenEmpty")} description={t("settings.governance.goldenEmptyDescription")} />
          ) : (
            <Table data-testid="golden-documents">
              <TableHeader>
                <TableRow>
                  <TableHead>{t("settings.governance.document")}</TableHead>
                  <TableHead>{t("settings.governance.type")}</TableHead>
                  <TableHead>{t("settings.governance.note")}</TableHead>
                  <TableHead>{t("settings.governance.added")}</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {(golden.data ?? []).map((g) => (
                  <TableRow key={g.id}>
                    <TableCell>
                      <Link to="/documents/$documentId" params={{ documentId: g.documentId }} className="underline-offset-2 hover:underline">
                        {g.documentId.slice(0, 8)}
                      </Link>
                    </TableCell>
                    <TableCell>{g.typeKey}</TableCell>
                    <TableCell>{g.note ?? "—"}</TableCell>
                    <TableCell>{formatDateTime(g.addedAt, i18n.language)}</TableCell>
                    <TableCell className="text-right">
                      <Button size="icon-xs" variant="ghost" aria-label={t("settings.governance.goldenRemove")} onClick={() => setRemoving(g.id)}>
                        <Trash2 aria-hidden />
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("settings.governance.runsTitle")}</CardTitle>
          <CardDescription>{t("settings.governance.runsDescription")}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1">
              <Label htmlFor="gov-supplier">{t("settings.governance.supplier")}</Label>
              <Select value={supplierId} onValueChange={(v) => { setSupplierId(v); setVersionId(""); }}>
                <SelectTrigger id="gov-supplier">
                  <SelectValue placeholder={t("settings.governance.pickSupplier")} />
                </SelectTrigger>
                <SelectContent>
                  {suppliers.items.map((s) => (
                    <SelectItem key={s.id} value={s.id}>
                      {s.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="gov-version">{t("settings.governance.version")}</Label>
              <Select value={versionId} onValueChange={setVersionId} disabled={versions.length === 0}>
                <SelectTrigger id="gov-version">
                  <SelectValue placeholder={t("settings.governance.pickVersion")} />
                </SelectTrigger>
                <SelectContent>
                  {versions.map(({ layout, version }) => (
                    <SelectItem key={version.id} value={version.id}>
                      {layout.layoutKey} v{String(version.version)} · {version.status}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="flex flex-col gap-1 sm:col-span-2">
              <Label htmlFor="gov-document">{t("settings.governance.documentId")}</Label>
              <Input id="gov-document" value={documentId} onChange={(e) => setDocumentId(e.target.value)} className="font-mono" placeholder="00000000-…" />
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="gov-rule">{t("settings.governance.draftRule")}</Label>
              <Textarea id="gov-rule" value={draftRule} onChange={(e) => setDraftRule(e.target.value)} rows={3} className="font-mono text-xs" spellCheck={false} placeholder='{"all":[{"text":"Rechnung"}]}' />
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="gov-mapping">{t("settings.governance.draftMapping")}</Label>
              <Textarea id="gov-mapping" value={draftMapping} onChange={(e) => setDraftMapping(e.target.value)} rows={3} className="font-mono text-xs" spellCheck={false} />
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => sandbox.mutate()} disabled={sandbox.isPending || !documentId.trim() || (!versionId && !draftRule.trim())} data-testid="sandbox-run">
              {sandbox.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <FlaskConical aria-hidden />}
              {t("settings.governance.sandbox")}
            </Button>
            <Button variant="outline" onClick={() => regression.mutate()} disabled={regression.isPending || !chosen} data-testid="regression-run">
              {regression.isPending && <Loader2 className="animate-spin" aria-hidden />}
              {t("settings.governance.regression")}
            </Button>
          </div>
          {run && <RunResult run={run} />}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("settings.governance.bundleTitle")}</CardTitle>
          <CardDescription>{t("settings.governance.bundleDescription")}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <fieldset className="flex flex-wrap gap-3">
            <legend className="mb-1 text-sm font-medium">{t("settings.governance.include")}</legend>
            {BUNDLE_PARTS.map((part) => (
              <label key={part} className="flex items-center gap-2 text-sm">
                <Checkbox checked={include.includes(part)} onCheckedChange={(c) => setInclude((current) => (c === true ? [...current, part] : current.filter((p) => p !== part)))} />
                {t(`settings.governance.parts.${part}`)}
              </label>
            ))}
          </fieldset>
          <div>
            <Button variant="secondary" onClick={() => exportBundle.mutate()} disabled={exportBundle.isPending || include.length === 0} data-testid="bundle-export">
              {exportBundle.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Download aria-hidden />}
              {t("settings.governance.export")}
            </Button>
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor="bundle-file">{t("settings.governance.importFile")}</Label>
            <Input
              id="bundle-file"
              type="file"
              accept="application/json,.json"
              onChange={(event) => {
                const file = event.target.files?.[0];
                setImported(null);
                if (file) void file.text().then(setBundleText);
              }}
              data-testid="bundle-file"
            />
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={() => importBundle.mutate("dry-run")} disabled={!bundleText || importBundle.isPending} data-testid="bundle-dry-run">
              {importBundle.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Upload aria-hidden />}
              {t("settings.governance.dryRun")}
            </Button>
            <Button onClick={() => importBundle.mutate("apply")} disabled={!bundleText || imported?.mode !== "dry-run" || importBundle.isPending} data-testid="bundle-apply">
              {t("settings.governance.apply")}
            </Button>
          </div>
          {imported && (
            <div className="flex flex-col gap-1 text-sm" data-testid="bundle-import-result">
              <span>{imported.signatureValid ? t("settings.governance.signatureValid") : t("settings.governance.signatureInvalid")}</span>
              <ul className="flex flex-col gap-0.5">
                {imported.items.map((item) => (
                  <li key={`${item.kind}:${item.key}`} className="flex gap-2">
                    <Badge variant={item.action === "error" ? "destructive" : item.action === "skip" ? "secondary" : "outline"}>{t(`settings.governance.actions.${item.action}`, { defaultValue: item.action })}</Badge>
                    <span>
                      {item.kind} <span className="font-mono">{item.key}</span>
                    </span>
                    {item.detail && <span className="text-muted-foreground">{item.detail}</span>}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </CardContent>
      </Card>
      <ConfirmDialog open={removing !== null} onOpenChange={(o) => !o && setRemoving(null)} title={t("settings.governance.goldenRemoveTitle")} confirmLabel={t("settings.governance.goldenRemove")} destructive onConfirm={() => (removing ? remove.mutateAsync(removing).then(() => setRemoving(null)) : undefined)} />
    </div>
  );
}
