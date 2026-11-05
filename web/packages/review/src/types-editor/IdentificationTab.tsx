import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Ban, CheckCircle2, FilePlus2, FlaskConical, Plus, Save } from "lucide-react";
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
  Input,
  Label,
  ScrollArea,
  Skeleton,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  Textarea,
} from "@docproc/ui";
import { ConfirmDialog, isApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { REVIEW_NS } from "../lib/labels";
import { draftOf, otherTypesTest, overrideRate, parseRule, ruleText, versionsOf, type TenantRule, type TenantRuleTest, type TenantRuleVersion } from "./identRules";

const EXAMPLE_RULE = '{\n  "all": [{ "text": "Preisliste", "page": 1, "region": "title" }]\n}';

/** `GET /document-types/{key}/identification-rules`: the tenant fallback rules of a type (docs/11 §8.5). */
export function useTenantRules(typeKey: string, enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.identificationRules.ofType(typeKey),
    queryFn: async () => unwrap(await client.GET("/api/v1/document-types/{key}/identification-rules", { params: { path: { key: typeKey } } })),
    enabled,
  });
}

/**
 * The type editor's Identification tab (docs/11 §8.5, §19 item 2): the tenant fallback rules of the type, which type a
 * document of an unknown supplier, or of one without layouts, before the keywords and the LLM. Each rule with its
 * versions (state, the rule in words, the save-time test "would have matched N documents of this type, M others"),
 * its excluded suppliers, status and override count; a ConfigAdmin creates a rule, edits its Draft, tests it, activates
 * it (confirming when the test matched other types) and disables it.
 */
export function IdentificationTab({ typeKey, canEdit }: { typeKey: string; canEdit: boolean }) {
  const { t } = useTranslation(REVIEW_NS);
  const rules = useTenantRules(typeKey);
  const [creating, setCreating] = useState(false);
  return (
    <div className="grid gap-4" data-testid="identification-tab">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="max-w-2xl space-y-1">
          <h2 className="text-base font-semibold">{t("identRules.title")}</h2>
          <p className="text-sm text-muted-foreground">{t("identRules.description")}</p>
        </div>
        {canEdit && !creating && (
          <Button type="button" onClick={() => setCreating(true)} data-testid="ident-rule-new">
            <Plus aria-hidden />
            {t("identRules.new")}
          </Button>
        )}
      </div>
      {creating && <RuleEditor typeKey={typeKey} onDone={() => setCreating(false)} />}
      {rules.isPending ? (
        <Skeleton className="h-32 w-full" aria-busy />
      ) : rules.isError ? (
        <Alert variant="destructive">
          <AlertDescription>{isApiError(rules.error) ? (rules.error.problem?.detail ?? rules.error.title) : t("types.failed")}</AlertDescription>
        </Alert>
      ) : (rules.data ?? []).length === 0 ? (
        <p className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">{t("identRules.none")}</p>
      ) : (
        (rules.data ?? []).map((rule) => <RuleCard key={rule.id} typeKey={typeKey} rule={rule} canEdit={canEdit} />)
      )}
    </div>
  );
}

function useSuppliersForExclusion() {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.suppliers.list({ limit: 200 }), queryFn: async () => unwrap(await client.GET("/api/v1/suppliers", { params: { query: { limit: 200 } } })) });
}

/** The excluded suppliers: a filtered list of the tenant's suppliers with a checkbox each. */
function ExcludedSuppliers({ value, onChange, disabled }: { value: string[]; onChange: (ids: string[]) => void; disabled?: boolean }) {
  const { t } = useTranslation(REVIEW_NS);
  const suppliers = useSuppliersForExclusion();
  const [filter, setFilter] = useState("");
  const shown = (suppliers.data?.items ?? []).filter((s) => s.name.toLowerCase().includes(filter.toLowerCase()));
  return (
    <div className="grid gap-1.5">
      <Label>{t("identRules.excluded", { count: value.length })}</Label>
      <Input placeholder={t("identRules.filterSuppliers")} value={filter} onChange={(e) => setFilter(e.target.value)} disabled={disabled} />
      <ScrollArea className="h-32 rounded-md border p-2">
        {shown.map((s) => (
          <Label key={s.id} className="flex items-center gap-2 py-0.5 text-sm">
            <Checkbox checked={value.includes(s.id)} disabled={disabled} onCheckedChange={(on) => onChange(on === true ? [...value, s.id] : value.filter((id) => id !== s.id))} />
            {s.name}
          </Label>
        ))}
      </ScrollArea>
    </div>
  );
}

/** A new rule (name, priority, rule, excluded suppliers): created as version 1, a Draft with its save-time test. */
function RuleEditor({ typeKey, onDone }: { typeKey: string; onDone: () => void }) {
  const { t } = useTranslation(REVIEW_NS);
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [priority, setPriority] = useState("100");
  const [text, setText] = useState(EXAMPLE_RULE);
  const [excluded, setExcluded] = useState<string[]>([]);
  const parsed = parseRule(text);
  const create = useMutation({
    mutationFn: async () =>
      unwrap(
        await client.POST("/api/v1/document-types/{key}/identification-rules", {
          params: { path: { key: typeKey } },
          body: { name: name.trim(), priority: Number.parseInt(priority, 10) || 100, rule: ("rule" in parsed ? parsed.rule : {}) as never, excludedSupplierIds: excluded },
        }),
      ),
    onSuccess: async (rule) => {
      toast.success(t("identRules.created", { name: rule.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.identificationRules.all });
      onDone();
    },
  });
  return (
    <Card data-testid="ident-rule-editor">
      <CardHeader>
        <CardTitle className="text-base">{t("identRules.newTitle")}</CardTitle>
      </CardHeader>
      <CardContent className="grid gap-3">
        <div className="grid gap-3 sm:grid-cols-[1fr_8rem]">
          <div className="grid gap-1.5">
            <Label htmlFor="ident-rule-name">{t("identRules.name")}</Label>
            <Input id="ident-rule-name" value={name} maxLength={200} onChange={(e) => setName(e.target.value)} data-testid="ident-rule-name" />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="ident-rule-priority">{t("identRules.priority")}</Label>
            <Input id="ident-rule-priority" inputMode="numeric" value={priority} onChange={(e) => setPriority(e.target.value)} />
          </div>
        </div>
        <RuleText text={text} onChange={setText} />
        <ExcludedSuppliers value={excluded} onChange={setExcluded} />
        <div className="flex gap-2">
          <Button type="button" onClick={() => create.mutate()} disabled={!name.trim() || "error" in parsed || create.isPending} aria-busy={create.isPending} data-testid="ident-rule-create">
            <FilePlus2 aria-hidden />
            {t("identRules.create")}
          </Button>
          <Button type="button" variant="ghost" onClick={onDone}>
            {t("types.cancel")}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}

function RuleText({ text, onChange, disabled }: { text: string; onChange: (text: string) => void; disabled?: boolean }) {
  const { t } = useTranslation(REVIEW_NS);
  const parsed = parseRule(text);
  return (
    <div className="grid gap-1.5">
      <Label>{t("identRules.rule")}</Label>
      <Textarea className="min-h-32 font-mono text-xs" spellCheck={false} value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} data-testid="ident-rule-json" aria-invalid={"error" in parsed ? true : undefined} />
      <p className={"error" in parsed ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>{"error" in parsed ? t(`identRules.ruleErrors.${parsed.error}`) : t("identRules.ruleHint")}</p>
    </div>
  );
}

export function TestSummary({ test }: { test: TenantRuleTest | null | undefined }) {
  const { t } = useTranslation(REVIEW_NS);
  if (!test) return <span className="text-xs text-muted-foreground">{t("identRules.notTested")}</span>;
  return (
    <span className={Number(test.otherTypes) > 0 ? "text-xs text-destructive" : "text-xs text-emerald-700 dark:text-emerald-400"} data-testid="ident-rule-test-summary">
      {t("identRules.testSummary", { matched: Number(test.sameType), others: Number(test.otherTypes), tested: Number(test.tested) })}
    </span>
  );
}

function RuleCard({ typeKey, rule, canEdit }: { typeKey: string; rule: TenantRule; canEdit: boolean }) {
  const { t } = useTranslation(REVIEW_NS);
  const { client } = useApi();
  const queryClient = useQueryClient();
  const draft = draftOf(rule);
  const [editing, setEditing] = useState<{ text: string; excluded: string[] } | null>(null);
  const [confirm, setConfirm] = useState<TenantRuleTest | null>(null);
  const invalidate = () => queryClient.invalidateQueries({ queryKey: queryKeys.identificationRules.ofType(typeKey) });
  const rate = overrideRate(rule);

  const newVersion = useMutation({
    mutationFn: async () => {
      const base = versionsOf(rule)[0];
      return unwrap(await client.POST("/api/v1/identification-rules/{id}/versions", { params: { path: { id: rule.id } }, body: { rule: (base?.rule ?? {}) as never, excludedSupplierIds: base?.excludedSupplierIds ?? [] } }));
    },
    onSuccess: async () => {
      await invalidate();
    },
  });
  const save = useMutation({
    mutationFn: async (version: TenantRuleVersion) => {
      const parsed = parseRule(editing!.text);
      if ("error" in parsed) throw new Error(t(`identRules.ruleErrors.${parsed.error}`));
      return unwrap(
        await client.PUT("/api/v1/identification-rules/{id}/versions/{version}", {
          params: { path: { id: rule.id, version: Number(version.version) } },
          body: { rule: parsed.rule as never, excludedSupplierIds: editing!.excluded },
          headers: { "If-Match": `"${Number(version.revision)}"` },
        }),
      );
    },
    onSuccess: async () => {
      toast.success(t("identRules.saved"));
      setEditing(null);
      await invalidate();
    },
  });
  const test = useMutation({
    mutationFn: async (version: TenantRuleVersion) => unwrap(await client.POST("/api/v1/identification-rules/{id}/versions/{version}:test", { params: { path: { id: rule.id, version: Number(version.version) } } })),
    onSuccess: async () => {
      await invalidate();
    },
  });
  const activate = useMutation({
    mutationFn: async ({ version, confirmOtherTypes }: { version: TenantRuleVersion; confirmOtherTypes: boolean }) =>
      unwrap(await client.POST("/api/v1/identification-rules/{id}/versions/{version}:activate", { params: { path: { id: rule.id, version: Number(version.version) } }, body: { confirmOtherTypes } })),
    meta: { silent: true },
    onSuccess: async () => {
      toast.success(t("identRules.activated", { name: rule.name }));
      setConfirm(null);
      await invalidate();
    },
    onError: (error) => {
      const other = isApiError(error) && error.isConflict ? otherTypesTest(error.problem) : null;
      if (other) setConfirm(other);
      else toast.error(isApiError(error) ? error.title : String(error), { description: isApiError(error) ? (error.problem?.detail ?? undefined) : undefined });
    },
  });
  const disable = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/identification-rules/{id}:disable", { params: { path: { id: rule.id } } })),
    onSuccess: async () => {
      toast.success(t("identRules.disabled", { name: rule.name }));
      await invalidate();
    },
  });

  const versions = useMemo(() => versionsOf(rule), [rule]);
  return (
    <Card data-testid={`ident-rule-${rule.name}`} data-status={rule.status}>
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
        <div className="space-y-1">
          <CardTitle className="flex flex-wrap items-center gap-2 text-base">
            {rule.name}
            <Badge variant={rule.status === "Active" ? "success" : "secondary"}>{t(`identRules.statuses.${rule.status}`, { defaultValue: rule.status })}</Badge>
            {rule.activeVersion !== null && <Badge variant="outline">{t("identRules.activeVersion", { version: Number(rule.activeVersion) })}</Badge>}
          </CardTitle>
          <CardDescription>
            {t("identRules.stats", { priority: Number(rule.priority), matched: Number(rule.matched), overridden: Number(rule.overridden) })}
            {rate >= 0.1 && (
              <Badge variant="warning" className="ml-2">
                {t("identRules.oftenOverridden", { percent: Math.round(rate * 100) })}
              </Badge>
            )}
          </CardDescription>
        </div>
        {canEdit && (
          <div className="flex flex-wrap gap-2">
            {!draft && (
              <Button type="button" size="sm" variant="outline" onClick={() => newVersion.mutate()} disabled={newVersion.isPending} data-testid="ident-rule-new-version">
                <FilePlus2 aria-hidden />
                {t("identRules.newVersion")}
              </Button>
            )}
            {rule.status !== "Disabled" && (
              <Button type="button" size="sm" variant="ghost" onClick={() => disable.mutate()} disabled={disable.isPending} data-testid="ident-rule-disable">
                <Ban aria-hidden />
                {t("identRules.disable")}
              </Button>
            )}
          </div>
        )}
      </CardHeader>
      <CardContent className="grid gap-3">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("identRules.columns.version")}</TableHead>
              <TableHead>{t("identRules.columns.state")}</TableHead>
              <TableHead>{t("identRules.columns.rule")}</TableHead>
              <TableHead>{t("identRules.columns.test")}</TableHead>
              <TableHead>{t("identRules.columns.excluded")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {versions.map((version) => {
              const isDraft = version.state === "Draft";
              return (
                <TableRow key={version.id} data-version={Number(version.version)} data-state={version.state}>
                  <TableCell>v{Number(version.version)}</TableCell>
                  <TableCell>
                    <Badge variant={version.state === "Active" ? "success" : isDraft ? "warning" : "secondary"}>{t(`identRules.states.${version.state}`, { defaultValue: version.state })}</Badge>
                  </TableCell>
                  <TableCell className="max-w-sm text-sm">{version.describe}</TableCell>
                  <TableCell>
                    <TestSummary test={version.test} />
                  </TableCell>
                  <TableCell>{version.excludedSupplierIds.length}</TableCell>
                  <TableCell>
                    {canEdit && isDraft && (
                      <div className="flex flex-wrap justify-end gap-1">
                        <Button type="button" size="xs" variant="ghost" onClick={() => setEditing({ text: ruleText(version.rule), excluded: [...version.excludedSupplierIds] })} data-testid="ident-rule-edit">
                          {t("identRules.edit")}
                        </Button>
                        <Button type="button" size="xs" variant="outline" onClick={() => test.mutate(version)} disabled={test.isPending} data-testid="ident-rule-test">
                          <FlaskConical aria-hidden />
                          {t("identRules.test")}
                        </Button>
                        <Button type="button" size="xs" onClick={() => activate.mutate({ version, confirmOtherTypes: false })} disabled={activate.isPending} data-testid="ident-rule-activate">
                          <CheckCircle2 aria-hidden />
                          {t("identRules.activate")}
                        </Button>
                      </div>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
        {editing && draft && (
          <div className="grid gap-3 rounded-md border p-3" data-testid="ident-rule-draft-editor">
            <RuleText text={editing.text} onChange={(text) => setEditing({ ...editing, text })} />
            <ExcludedSuppliers value={editing.excluded} onChange={(excluded) => setEditing({ ...editing, excluded })} />
            <div className="flex gap-2">
              <Button type="button" size="sm" onClick={() => save.mutate(draft)} disabled={save.isPending || "error" in parseRule(editing.text)} data-testid="ident-rule-save">
                <Save aria-hidden />
                {t("identRules.save")}
              </Button>
              <Button type="button" size="sm" variant="ghost" onClick={() => setEditing(null)}>
                {t("types.cancel")}
              </Button>
            </div>
          </div>
        )}
        {(versions[0]?.test?.otherTypeMatches.length ?? 0) > 0 && (
          <Alert variant="destructive">
            <AlertTitle>{t("identRules.otherTypesTitle")}</AlertTitle>
            <AlertDescription>
              <ul className="list-disc pl-4 text-xs">
                {versions[0]!.test!.otherTypeMatches.map((m) => (
                  <li key={m.documentId}>
                    <a className="underline underline-offset-2" href={`/documents/${m.documentId}`}>
                      {m.fileName ?? m.documentId}
                    </a>{" "}
                    · {m.typeKey}
                  </li>
                ))}
              </ul>
            </AlertDescription>
          </Alert>
        )}
      </CardContent>
      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={t("identRules.confirmTitle")}
        description={t("identRules.confirm", { summary: confirm?.summary ?? "" })}
        confirmLabel={t("identRules.activateAnyway")}
        destructive
        isPending={activate.isPending}
        onConfirm={() => (draft ? activate.mutateAsync({ version: draft, confirmOtherTypes: true }).then(() => undefined) : Promise.resolve())}
      />
    </Card>
  );
}
