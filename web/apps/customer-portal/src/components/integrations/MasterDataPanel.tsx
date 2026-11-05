import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { FlaskConical, History, Pencil, Play, Plus, Trash2, Upload } from "lucide-react";
import {
  Alert,
  AlertDescription,
  AlertTitle,
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Checkbox,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  Textarea,
  Toggle,
} from "@docproc/ui";
import { ApiError, ConfirmDialog, formatDateTime, queryKeys, relativeTime, unwrap, useApi } from "@docproc/app-shell";
import { SectionIntro } from "@/components/settings/SectionIntro";
import { useDestinations } from "./api";
import { conflictPolicies, csvHeader, defaultMatchKeys, guessColumns, importBody, importTargets, reportOf, type ConflictPolicy, type MasterDataRun, type MasterDataSource, type Report } from "./masterData";

const SKIP = "__skip__";
const DEFAULT = "__default__";
const TARGETS = [...importTargets, "attributes.erpVendorId"] as const;

/**
 * Settings > Integrations > Master data (docs/11 §14, §19 item 4 "Master data sources and conflicts"): a one-off supplier
 * import from CSV (with a column map) or JSON, checked first as a dry run and then applied, with its report (created,
 * updated, unchanged, conflicts and errors per row); and the scheduled sources (an http destination read on a cron
 * schedule, or an upload source) with Run, Dry run and their runs. Tenant admins only.
 */
export function MasterDataPanel() {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-6" data-testid="master-data-panel">
      <SectionIntro title={t("masterData.title")} description={t("masterData.description")} />
      <ImportCard />
      <SourcesCard />
    </div>
  );
}

function ImportCard() {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [format, setFormat] = useState<"csv" | "json">("csv");
  const [content, setContent] = useState("");
  const [columns, setColumns] = useState<Record<string, string>>({});
  const [matchKeys, setMatchKeys] = useState<string[]>([...defaultMatchKeys]);
  const [policy, setPolicy] = useState<Record<string, ConflictPolicy>>({});
  const [run, setRun] = useState<MasterDataRun | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const header = useMemo(() => (format === "csv" ? csvHeader(content) : []), [format, content]);

  const changeContent = (text: string) => {
    setContent(text);
    setRun(null);
    if (format === "csv" && Object.keys(columns).length === 0) setColumns(guessColumns(csvHeader(text)));
  };
  const submit = useMutation({
    mutationFn: async (dryRun: boolean) => unwrap(await client.POST("/api/v1/suppliers:import", { body: importBody({ format, content, columns, matchKeys, policy, dryRun }) as never })),
    meta: { silent: true },
    onSuccess: async (result) => {
      setErrors([]);
      setRun(result);
      if (!result.dryRun) {
        toast.success(t("masterData.imported", { created: Number(result.created), updated: Number(result.updated) }));
        await queryClient.invalidateQueries({ queryKey: queryKeys.suppliers.all });
      }
    },
    onError: (error) => {
      setErrors(error instanceof ApiError && error.isValidation ? Object.entries(error.fieldErrors).map(([k, v]) => `${k}: ${v.join(" ")}`) : [error instanceof ApiError ? (error.problem?.detail ?? error.title) : String(error)]);
    },
  });

  return (
    <Card data-testid="master-data-import">
      <CardHeader>
        <CardTitle className="text-base">{t("masterData.import.title")}</CardTitle>
        <CardDescription>{t("masterData.import.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="flex flex-wrap items-end gap-3">
          <div className="grid gap-1">
            <Label className="text-xs">{t("masterData.import.format")}</Label>
            <Select
              value={format}
              onValueChange={(value) => {
                setFormat(value as "csv" | "json");
                setRun(null);
              }}
            >
              <SelectTrigger className="w-32" data-testid="import-format">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="csv">CSV</SelectItem>
                <SelectItem value="json">JSON</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="grid gap-1">
            <Label htmlFor="import-file" className="text-xs">
              {t("masterData.import.file")}
            </Label>
            <Input
              id="import-file"
              type="file"
              accept={format === "csv" ? ".csv,text/csv" : ".json,application/json"}
              className="w-72"
              onChange={(event) => {
                const file = event.target.files?.[0];
                if (file) void file.text().then(changeContent);
              }}
            />
          </div>
        </div>
        <div className="grid gap-1">
          <Label htmlFor="import-content" className="text-xs">
            {format === "csv" ? t("masterData.import.csv") : t("masterData.import.json")}
          </Label>
          <Textarea id="import-content" className="min-h-28 font-mono text-xs" spellCheck={false} value={content} onChange={(event) => changeContent(event.target.value)} data-testid="import-content" />
        </div>
        {format === "csv" && header.length > 0 && (
          <Table data-testid="import-columns">
            <TableHeader>
              <TableRow>
                <TableHead>{t("masterData.import.target")}</TableHead>
                <TableHead>{t("masterData.import.column")}</TableHead>
                <TableHead>{t("masterData.import.policy")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {TARGETS.map((target) => (
                <TableRow key={target} data-target={target}>
                  <TableCell className="font-mono text-xs">{target}</TableCell>
                  <TableCell>
                    <Select
                      value={columns[target] ?? SKIP}
                      onValueChange={(value) =>
                        setColumns((current) => {
                          const next = { ...current };
                          if (value === SKIP) delete next[target];
                          else next[target] = value;
                          return next;
                        })
                      }
                    >
                      <SelectTrigger className="w-56" aria-label={t("masterData.import.columnOf", { target })}>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={SKIP}>{t("masterData.import.skip")}</SelectItem>
                        {header.map((h) => (
                          <SelectItem key={h} value={h}>
                            {h}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </TableCell>
                  <TableCell>
                    <PolicySelect value={policy[target]} onChange={(value) => setPolicy((current) => withPolicy(current, target, value))} label={t("masterData.import.policyOf", { target })} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        <div className="grid gap-1.5">
          <Label className="text-xs">{t("masterData.matchKeys")}</Label>
          <div className="flex flex-wrap gap-4">
            {defaultMatchKeys.map((key) => (
              <Label key={key} className="flex items-center gap-2 font-mono text-xs">
                <Checkbox checked={matchKeys.includes(key)} onCheckedChange={(on) => setMatchKeys((current) => (on === true ? defaultMatchKeys.filter((k) => current.includes(k) || k === key) : current.filter((k) => k !== key)))} />
                {key}
              </Label>
            ))}
          </div>
        </div>
        {errors.length > 0 && (
          <Alert variant="destructive" data-testid="import-errors">
            <AlertDescription>
              <ul className="list-disc pl-4">
                {errors.map((e) => (
                  <li key={e}>{e}</li>
                ))}
              </ul>
            </AlertDescription>
          </Alert>
        )}
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="secondary" disabled={!content.trim() || submit.isPending} onClick={() => submit.mutate(true)} data-testid="import-dry-run">
            <FlaskConical aria-hidden />
            {t("masterData.dryRun")}
          </Button>
          <Button type="button" disabled={!content.trim() || submit.isPending || !run?.dryRun} title={!run?.dryRun ? t("masterData.import.checkFirst") : undefined} onClick={() => submit.mutate(false)} data-testid="import-apply">
            <Upload aria-hidden />
            {t("masterData.import.apply")}
          </Button>
        </div>
        {run && <RunReport run={run} />}
      </CardContent>
    </Card>
  );
}

function withPolicy(current: Record<string, ConflictPolicy>, target: string, value: ConflictPolicy | null): Record<string, ConflictPolicy> {
  const next = { ...current };
  if (value === null) delete next[target];
  else next[target] = value;
  return next;
}

function PolicySelect({ value, onChange, label }: { value: ConflictPolicy | undefined; onChange: (value: ConflictPolicy | null) => void; label: string }) {
  const { t } = useTranslation();
  return (
    <Select value={value ?? DEFAULT} onValueChange={(next) => onChange(next === DEFAULT ? null : (next as ConflictPolicy))}>
      <SelectTrigger className="w-44" aria-label={label}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={DEFAULT}>{t("masterData.policies.default")}</SelectItem>
        {conflictPolicies.map((p) => (
          <SelectItem key={p} value={p}>
            {t(`masterData.policies.${p}`)}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

const actionVariant: Record<string, "success" | "secondary" | "outline" | "warning" | "destructive"> = { create: "success", update: "secondary", unchanged: "outline", conflict: "warning", error: "destructive" };

/** A run's report: the counts and one row per record, conflicts and errors first when filtered. */
export function RunReport({ run }: { run: MasterDataRun }) {
  const { t, i18n } = useTranslation();
  const report: Report = reportOf(run);
  const [problemsOnly, setProblemsOnly] = useState(report.conflicts + report.errors > 0);
  const rows = problemsOnly ? report.rows.filter((r) => r.action === "conflict" || r.action === "error") : report.rows;
  return (
    <div className="grid gap-3" data-testid="master-data-report" data-dry-run={run.dryRun}>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <Badge variant={run.dryRun ? "outline" : "secondary"}>{run.dryRun ? t("masterData.report.dryRun") : t("masterData.report.applied")}</Badge>
        <Badge variant="outline">{t(`masterData.statuses.${run.status}`, { defaultValue: run.status })}</Badge>
        <span className="text-muted-foreground">{formatDateTime(run.at, i18n.language)}</span>
        {(["created", "updated", "unchanged", "conflicts", "errors"] as const).map((key) => (
          <Badge key={key} variant={key === "errors" && report.errors > 0 ? "destructive" : key === "conflicts" && report.conflicts > 0 ? "warning" : "outline"} data-count={key}>
            {t(`masterData.report.${key}`, { count: report[key] })}
          </Badge>
        ))}
        <Toggle size="sm" variant="outline" pressed={problemsOnly} onPressedChange={setProblemsOnly}>
          {t("masterData.report.problemsOnly")}
        </Toggle>
      </div>
      {report.failure && (
        <Alert variant="destructive">
          <AlertTitle>{t("masterData.report.failed")}</AlertTitle>
          <AlertDescription>{report.failure}</AlertDescription>
        </Alert>
      )}
      {rows.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("masterData.report.row")}</TableHead>
              <TableHead>{t("masterData.report.action")}</TableHead>
              <TableHead>{t("masterData.report.supplier")}</TableHead>
              <TableHead>{t("masterData.report.matchedBy")}</TableHead>
              <TableHead>{t("masterData.report.details")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.slice(0, 200).map((row) => (
              <TableRow key={row.row} data-action={row.action}>
                <TableCell className="text-xs tabular-nums">{row.row}</TableCell>
                <TableCell>
                  <Badge variant={actionVariant[row.action] ?? "outline"}>{t(`masterData.actions.${row.action}`, { defaultValue: row.action })}</Badge>
                </TableCell>
                <TableCell className="text-sm">{row.name ?? "—"}</TableCell>
                <TableCell className="font-mono text-xs">{row.matchedBy ?? "—"}</TableCell>
                <TableCell className="text-xs">{row.error ?? [...row.changes, ...row.conflicts.map((c) => t("masterData.report.conflictOn", { attribute: c }))].join(", ")}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function useSources() {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.masterData.sources(), queryFn: async () => unwrap(await client.GET("/api/v1/master-data-sources")) });
}

function SourcesCard() {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const sources = useSources();
  const [editing, setEditing] = useState<MasterDataSource | "new" | null>(null);
  const [running, setRunning] = useState<{ source: MasterDataSource; dryRun: boolean } | null>(null);
  const [history, setHistory] = useState<MasterDataSource | null>(null);
  const [deleting, setDeleting] = useState<MasterDataSource | null>(null);
  const invalidate = () => queryClient.invalidateQueries({ queryKey: queryKeys.masterData.all });
  const remove = useMutation({
    mutationFn: async (id: string) => unwrap(await client.DELETE("/api/v1/master-data-sources/{id}", { params: { path: { id } } })),
    onSuccess: async () => {
      setDeleting(null);
      await invalidate();
    },
  });

  return (
    <Card data-testid="master-data-sources">
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
        <div className="space-y-1.5">
          <CardTitle className="text-base">{t("masterData.sources.title")}</CardTitle>
          <CardDescription>{t("masterData.sources.description")}</CardDescription>
        </div>
        <Button type="button" size="sm" onClick={() => setEditing("new")} data-testid="source-new">
          <Plus aria-hidden />
          {t("masterData.sources.new")}
        </Button>
      </CardHeader>
      <CardContent className="grid gap-3">
        {(sources.data ?? []).length === 0 ? (
          <p className="text-sm text-muted-foreground">{sources.isPending ? "…" : t("masterData.sources.none")}</p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("masterData.sources.name")}</TableHead>
                <TableHead>{t("masterData.sources.transport")}</TableHead>
                <TableHead>{t("masterData.sources.schedule")}</TableHead>
                <TableHead>{t("masterData.sources.lastRun")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {(sources.data ?? []).map((source) => (
                <TableRow key={source.id} data-source={source.name}>
                  <TableCell className="font-medium">
                    {source.name} {!source.enabled && <Badge variant="secondary">{t("masterData.sources.disabled")}</Badge>}
                  </TableCell>
                  <TableCell>{t(`masterData.transports.${source.transport}`, { defaultValue: source.transport })}</TableCell>
                  <TableCell className="font-mono text-xs">
                    {source.schedule ?? "—"}
                    {source.nextRunAt && <span className="block font-sans text-muted-foreground">{t("masterData.sources.next", { when: relativeTime(source.nextRunAt, i18n.language) })}</span>}
                  </TableCell>
                  <TableCell className="text-xs">{source.lastRunAt ? relativeTime(source.lastRunAt, i18n.language) : "—"}</TableCell>
                  <TableCell>
                    <div className="flex flex-wrap justify-end gap-1">
                      <Button size="xs" variant="outline" onClick={() => setRunning({ source, dryRun: true })} data-testid={`source-dry-run-${source.name}`}>
                        <FlaskConical aria-hidden />
                        {t("masterData.dryRun")}
                      </Button>
                      <Button size="xs" onClick={() => setRunning({ source, dryRun: false })} data-testid={`source-run-${source.name}`}>
                        <Play aria-hidden />
                        {t("masterData.sources.run")}
                      </Button>
                      <Button size="xs" variant="ghost" onClick={() => setHistory(source)} data-testid={`source-runs-${source.name}`}>
                        <History aria-hidden />
                        {t("masterData.sources.runs")}
                      </Button>
                      <Button size="xs" variant="ghost" onClick={() => setEditing(source)} aria-label={t("masterData.sources.edit", { name: source.name })}>
                        <Pencil aria-hidden />
                      </Button>
                      <Button size="xs" variant="ghost" onClick={() => setDeleting(source)} aria-label={t("masterData.sources.delete", { name: source.name })}>
                        <Trash2 aria-hidden />
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {history && <SourceRuns source={history} onClose={() => setHistory(null)} />}
      </CardContent>
      {editing && <SourceDialog source={editing === "new" ? null : editing} onClose={() => setEditing(null)} onSaved={invalidate} />}
      {running && <RunDialog source={running.source} dryRun={running.dryRun} onClose={() => setRunning(null)} onDone={invalidate} />}
      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("masterData.sources.deleteTitle", { name: deleting?.name ?? "" })}
        description={t("masterData.sources.deleteDescription")}
        confirmLabel={t("integrations.delete")}
        destructive
        isPending={remove.isPending}
        onConfirm={() => remove.mutateAsync(deleting!.id).then(() => undefined)}
      />
    </Card>
  );
}

function SourceRuns({ source, onClose }: { source: MasterDataSource; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const runs = useQuery({ queryKey: queryKeys.masterData.runs(source.id), queryFn: async () => unwrap(await client.GET("/api/v1/master-data-sources/{id}/runs", { params: { path: { id: source.id } } })) });
  const [open, setOpen] = useState<string | null>(null);
  return (
    <div className="grid gap-2 rounded-md border p-3" data-testid="source-run-history">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold">{t("masterData.sources.runsOf", { name: source.name })}</h3>
        <Button size="xs" variant="ghost" onClick={onClose}>
          {t("integrations.back")}
        </Button>
      </div>
      {(runs.data ?? []).length === 0 && <p className="text-sm text-muted-foreground">{t("masterData.sources.noRuns")}</p>}
      {(runs.data ?? []).map((run) => (
        <div key={run.id} className="grid gap-2">
          <Button type="button" variant="ghost" size="sm" className="h-auto flex-wrap justify-start gap-2 px-1 py-0.5 text-left font-normal" onClick={() => setOpen(open === run.id ? null : run.id)}>
            <span>{formatDateTime(run.at, i18n.language)}</span>
            <Badge variant="outline">{t(`masterData.triggers.${run.trigger}`, { defaultValue: run.trigger })}</Badge>
            {run.dryRun && <Badge variant="outline">{t("masterData.report.dryRun")}</Badge>}
            <span className="text-muted-foreground">{t("masterData.report.summary", { created: Number(run.created), updated: Number(run.updated), conflicts: Number(run.conflicts), errors: Number(run.errors) })}</span>
          </Button>
          {open === run.id && <RunReport run={run} />}
        </div>
      ))}
    </div>
  );
}

/** Run or dry-run a source now; an upload source takes its records here. */
function RunDialog({ source, dryRun, onClose, onDone }: { source: MasterDataSource; dryRun: boolean; onClose: () => void; onDone: () => Promise<unknown> }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const [records, setRecords] = useState("[]");
  const [run, setRun] = useState<MasterDataRun | null>(null);
  const upload = source.transport === "upload";
  const parsed = useMemo(() => {
    try {
      return JSON.parse(records) as unknown;
    } catch {
      return undefined;
    }
  }, [records]);
  const go = useMutation({
    mutationFn: async () => {
      const options = { params: { path: { id: source.id } }, body: { records: (upload ? parsed : null) as never } };
      return unwrap(dryRun ? await client.POST("/api/v1/master-data-sources/{id}:dry-run", options) : await client.POST("/api/v1/master-data-sources/{id}:run", options));
    },
    onSuccess: async (result) => {
      setRun(result);
      await onDone();
    },
  });
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-3xl" data-testid="source-run-dialog">
        <DialogHeader>
          <DialogTitle>{dryRun ? t("masterData.sources.dryRunTitle", { name: source.name }) : t("masterData.sources.runTitle", { name: source.name })}</DialogTitle>
          <DialogDescription>{upload ? t("masterData.sources.uploadHint") : t("masterData.sources.httpHint")}</DialogDescription>
        </DialogHeader>
        {upload && <Textarea className="min-h-28 font-mono text-xs" spellCheck={false} value={records} onChange={(e) => setRecords(e.target.value)} aria-label={t("masterData.sources.records")} />}
        {run && <RunReport run={run} />}
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            {t("integrations.back")}
          </Button>
          <Button type="button" onClick={() => go.mutate()} disabled={go.isPending || (upload && parsed === undefined)} data-testid="source-run-go">
            {dryRun ? <FlaskConical aria-hidden /> : <Play aria-hidden />}
            {dryRun ? t("masterData.dryRun") : t("masterData.sources.run")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function SourceDialog({ source, onClose, onSaved }: { source: MasterDataSource | null; onClose: () => void; onSaved: () => Promise<unknown> }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const destinations = useDestinations();
  const [name, setName] = useState(source?.name ?? "");
  const [transport, setTransport] = useState(source?.transport ?? "http");
  const [destinationId, setDestinationId] = useState(source?.destinationId ?? "");
  const [mapping, setMapping] = useState(source?.mapping ?? "");
  const [schedule, setSchedule] = useState(source?.schedule ?? "");
  const [enabled, setEnabled] = useState(source?.enabled ?? true);
  const [matchKeys, setMatchKeys] = useState<string[]>(source?.matchKeys ?? [...defaultMatchKeys]);
  const [errors, setErrors] = useState<string[]>([]);
  const save = useMutation({
    mutationFn: async () => {
      const body = {
        name,
        target: "suppliers",
        transport,
        destinationId: transport === "http" ? destinationId || null : null,
        mapping: mapping.trim() || null,
        matchKeys,
        conflictPolicy: (source?.conflictPolicy as Record<string, string> | null) ?? null,
        schedule: schedule.trim() || null,
        enabled,
      };
      return source ? unwrap(await client.PATCH("/api/v1/master-data-sources/{id}", { params: { path: { id: source.id } }, body })) : unwrap(await client.POST("/api/v1/master-data-sources", { body }));
    },
    meta: { silent: true },
    onSuccess: async (saved) => {
      toast.success(t("masterData.sources.saved", { name: saved.name }));
      await onSaved();
      onClose();
    },
    onError: (error) => setErrors(error instanceof ApiError && error.isValidation ? Object.entries(error.fieldErrors).map(([k, v]) => `${k}: ${v.join(" ")}`) : [error instanceof ApiError ? (error.problem?.detail ?? error.title) : String(error)]),
  });
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl" data-testid="source-dialog">
        <DialogHeader>
          <DialogTitle>{source ? t("masterData.sources.editTitle", { name: source.name }) : t("masterData.sources.newTitle")}</DialogTitle>
          <DialogDescription>{t("masterData.sources.formDescription")}</DialogDescription>
        </DialogHeader>
        <div className="grid gap-3">
          <div className="grid gap-1">
            <Label htmlFor="source-name">{t("masterData.sources.name")}</Label>
            <Input id="source-name" value={name} onChange={(e) => setName(e.target.value)} data-testid="source-name" />
          </div>
          <div className="flex flex-wrap gap-3">
            <div className="grid gap-1">
              <Label className="text-xs">{t("masterData.sources.transport")}</Label>
              <Select value={transport} onValueChange={setTransport}>
                <SelectTrigger className="w-40" data-testid="source-transport">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="http">{t("masterData.transports.http")}</SelectItem>
                  <SelectItem value="upload">{t("masterData.transports.upload")}</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {transport === "http" && (
              <div className="grid gap-1">
                <Label className="text-xs">{t("masterData.sources.destination")}</Label>
                <Select value={destinationId} onValueChange={setDestinationId}>
                  <SelectTrigger className="w-64" data-testid="source-destination">
                    <SelectValue placeholder={t("masterData.sources.pickDestination")} />
                  </SelectTrigger>
                  <SelectContent>
                    {(destinations.data ?? [])
                      .filter((d) => d.transport === "http")
                      .map((d) => (
                        <SelectItem key={d.id} value={d.id}>
                          {d.name}
                        </SelectItem>
                      ))}
                  </SelectContent>
                </Select>
              </div>
            )}
          </div>
          <div className="grid gap-1">
            <Label htmlFor="source-schedule">{t("masterData.sources.schedule")}</Label>
            <Input id="source-schedule" className="font-mono text-xs" value={schedule} placeholder="CRON_TZ=Europe/Berlin 17 2 * * *" onChange={(e) => setSchedule(e.target.value)} data-testid="source-schedule" />
            <p className="text-xs text-muted-foreground">{t("masterData.sources.scheduleHint")}</p>
          </div>
          <div className="grid gap-1">
            <Label htmlFor="source-mapping">{t("masterData.sources.mapping")}</Label>
            <Textarea id="source-mapping" className="min-h-24 font-mono text-xs" spellCheck={false} value={mapping} placeholder='vendors.{ "name": name, "vatId": vat, "attributes": { "erpVendorId": no } }' onChange={(e) => setMapping(e.target.value)} />
          </div>
          <div className="grid gap-1.5">
            <Label className="text-xs">{t("masterData.matchKeys")}</Label>
            <div className="flex flex-wrap gap-4">
              {defaultMatchKeys.map((key) => (
                <Label key={key} className="flex items-center gap-2 font-mono text-xs">
                  <Checkbox checked={matchKeys.includes(key)} onCheckedChange={(on) => setMatchKeys((current) => (on === true ? defaultMatchKeys.filter((k) => current.includes(k) || k === key) : current.filter((k) => k !== key)))} />
                  {key}
                </Label>
              ))}
            </div>
          </div>
          <Label className="flex items-center gap-2 text-sm">
            <Switch checked={enabled} onCheckedChange={setEnabled} />
            {t("masterData.sources.enabled")}
          </Label>
          {errors.length > 0 && (
            <Alert variant="destructive">
              <AlertDescription>
                <ul className="list-disc pl-4">
                  {errors.map((e) => (
                    <li key={e}>{e}</li>
                  ))}
                </ul>
              </AlertDescription>
            </Alert>
          )}
        </div>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            {t("integrations.back")}
          </Button>
          <Button type="button" onClick={() => save.mutate()} disabled={!name.trim() || save.isPending} data-testid="source-save">
            {t("integrations.save")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
