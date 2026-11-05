import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, FlaskConical, Plus, Trash2 } from "lucide-react";
import {
  Alert,
  AlertDescription,
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
  cn,
} from "@docproc/ui";
import type { components } from "@docproc/api-client";
import { formatDateTime, unwrap, useApi } from "@docproc/app-shell";
import { REVIEW_NS } from "../lib/labels";
import { useRuleKinds } from "../pipeline/api";
import { ParamsForm } from "../pipeline/ParamsForm";
import type { JsonSchema } from "../pipeline/paramsSchema";
import type { Definition } from "./model";
import {
  addRule,
  dueByRoles,
  flagIssues,
  flagsOf,
  lineSources,
  moveRule,
  newFlagCode,
  priorities,
  removeFlag,
  removeRule,
  reviewOf,
  ruleIssues,
  rulesOf,
  setFlags,
  setReview,
  severities,
  updateRule,
  type FlagEntry,
  type ReviewGate,
} from "./rulesModel";

type RuleTestResponse = components["schemas"]["RuleTestResponse"];

const NONE = "__none__";

export interface RulesTabProps {
  typeKey: string;
  version: number;
  definition: Definition;
  editable: boolean;
  /** The definition has changes not saved yet: the test runs on the saved version. */
  dirty: boolean;
  onChange: (definition: Definition) => void;
}

/**
 * The type editor's Rules tab (docs/11 §11, §19 item 2): the rule list with a form per rule kind (the P5 kinds
 * `requiredIf`, `compare`, `sumEquals`, `arithmetic`, `expression` and the line-table kinds among them), the
 * definition's own flags (`X_*` codes with their messages), the review gate (`requirePassedRules`, `blockOnWarnings`,
 * `lines`, priority and `dueBy`), and a test of the saved version's checks against one of the type's documents.
 */
export function RulesTab({ typeKey, version, definition, editable, dirty, onChange }: RulesTabProps) {
  const { t } = useTranslation(REVIEW_NS);
  const kinds = useRuleKinds();
  const kindMap = useMemo(() => new Map((kinds.data ?? []).map((k) => [k.kind, k])), [kinds.data]);
  const required = useMemo(() => new Map((kinds.data ?? []).map((k) => [k.kind, { required: ((k.paramsSchema as JsonSchema | null)?.required ?? []) as string[] }])), [kinds.data]);
  const rules = rulesOf(definition);
  const flags = flagsOf(definition);
  const issues = ruleIssues(definition, required);
  const [selected, setSelected] = useState<number | null>(rules.length > 0 ? 0 : null);
  const [newKind, setNewKind] = useState<string>("compare");
  const rule = selected !== null ? rules[selected] : undefined;
  const kind = rule ? kindMap.get(rule.kind) : undefined;

  return (
    <div className="grid gap-4" data-testid="rules-tab">
      <div className="grid gap-4 lg:grid-cols-[minmax(16rem,1fr)_2fr]">
        <Card>
          <CardHeader>
            <CardTitle className="text-base">{t("rules.title", { count: rules.length })}</CardTitle>
            <CardDescription>{t("rules.description")}</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-2">
            {rules.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t("rules.none")}</p>
            ) : (
              <ul className="grid gap-1" aria-label={t("rules.listLabel")}>
                {rules.map((entry, index) => {
                  const bad = issues.some((i) => i.index === index);
                  return (
                    <li key={`${index}:${entry.id}`}>
                      <Button
                        type="button"
                        variant="ghost"
                        onClick={() => setSelected(index)}
                        className={cn("h-auto w-full justify-between gap-2 px-2 py-1.5 text-left font-normal", selected === index && "bg-muted")}
                        data-testid={`rule-${entry.id}`}
                        aria-current={selected === index ? "true" : undefined}
                      >
                        <span className="min-w-0">
                          <span className="block truncate font-mono text-xs">{entry.id}</span>
                          <span className="block truncate text-xs text-muted-foreground">{entry.kind}</span>
                        </span>
                        <span className="flex shrink-0 gap-1">
                          {entry.flag && <Badge variant="outline">{entry.flag}</Badge>}
                          {bad && <Badge variant="destructive">{t("rules.issue")}</Badge>}
                        </span>
                      </Button>
                    </li>
                  );
                })}
              </ul>
            )}
            {editable && (
              <div className="flex gap-2 pt-2">
                <Select value={newKind} onValueChange={setNewKind}>
                  <SelectTrigger className="flex-1" data-testid="rule-new-kind" aria-label={t("rules.kind")}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {(kinds.data ?? []).map((k) => (
                      <SelectItem key={k.kind} value={k.kind}>
                        {k.kind}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  onClick={() => {
                    const next = addRule(definition, newKind);
                    onChange(next.definition);
                    setSelected(next.index);
                  }}
                  data-testid="rule-add"
                >
                  <Plus aria-hidden />
                  {t("rules.add")}
                </Button>
              </div>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardContent className="grid gap-4 pt-6">
            {!rule || selected === null ? (
              <p className="text-sm text-muted-foreground">{t("rules.pick")}</p>
            ) : (
              <div className="grid gap-4" data-testid="rule-editor" key={`${selected}:${rule.kind}`}>
                {kind && <p className="text-sm text-muted-foreground">{kind.description}</p>}
                {issues
                  .filter((i) => i.index === selected)
                  .map((issue) => (
                    <p key={issue.code + (issue.detail ?? "")} className="text-sm text-destructive" role="alert">
                      {t(`rules.issues.${issue.code}`, { detail: issue.detail ?? "" })}
                    </p>
                  ))}
                <div className="grid gap-3 sm:grid-cols-2">
                  <div className="grid gap-1.5">
                    <Label htmlFor="rule-id">{t("rules.id")}</Label>
                    <Input id="rule-id" className="font-mono text-xs" value={rule.id} disabled={!editable} onChange={(e) => onChange(updateRule(definition, selected, { id: e.target.value }))} data-testid="rule-id" />
                  </div>
                  <div className="grid gap-1.5">
                    <Label>{t("rules.kind")}</Label>
                    <Input className="font-mono text-xs" value={rule.kind} disabled readOnly />
                  </div>
                  <div className="grid gap-1.5">
                    <Label>{t("rules.severity")}</Label>
                    <Select value={rule.severity ?? NONE} onValueChange={(value) => onChange(updateRule(definition, selected, { severity: value === NONE ? null : (value as "warning") }))} disabled={!editable}>
                      <SelectTrigger data-testid="rule-severity">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={NONE}>{t("rules.kindSeverity")}</SelectItem>
                        {severities.map((s) => (
                          <SelectItem key={s} value={s}>
                            {t(`rules.severities.${s}`)}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                  <div className="grid gap-1.5">
                    <Label>{t("rules.flag")}</Label>
                    <Select value={rule.flag ?? NONE} onValueChange={(value) => onChange(updateRule(definition, selected, { flag: value === NONE ? null : value }))} disabled={!editable}>
                      <SelectTrigger data-testid="rule-flag">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={NONE}>{t("rules.kindFlags")}</SelectItem>
                        {flags.map((f) => (
                          <SelectItem key={f.code} value={f.code}>
                            {f.code}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                </div>
                <div className="grid gap-1.5">
                  <Label htmlFor="rule-applies">{t("rules.appliesWhen")}</Label>
                  <Input id="rule-applies" className="font-mono text-xs" value={rule.appliesWhen ?? ""} disabled={!editable} placeholder={t("rules.appliesWhenHint")} onChange={(e) => onChange(updateRule(definition, selected, { appliesWhen: e.target.value }))} />
                </div>
                <div className="grid gap-3 sm:grid-cols-2">
                  {(["en", "de"] as const).map((locale) => (
                    <div key={locale} className="grid gap-1.5">
                      <Label htmlFor={`rule-message-${locale}`}>{t("rules.messageIn", { locale })}</Label>
                      <Input
                        id={`rule-message-${locale}`}
                        value={rule.message?.[locale] ?? ""}
                        disabled={!editable}
                        onChange={(e) => {
                          const message = { ...(rule.message ?? {}), [locale]: e.target.value };
                          if (!e.target.value) delete message[locale];
                          onChange(updateRule(definition, selected, { message: Object.keys(message).length ? message : null }));
                        }}
                      />
                    </div>
                  ))}
                </div>
                <div className="grid gap-2">
                  <h3 className="text-sm font-semibold">{t("rules.params")}</h3>
                  <ParamsForm
                    idPrefix="rule-param"
                    schema={kind?.paramsSchema as JsonSchema | undefined}
                    expressionParams={kind?.expressionParams ?? []}
                    values={(rule.params ?? {}) as Record<string, unknown>}
                    disabled={!editable}
                    onChange={(params) => onChange(updateRule(definition, selected, { params: Object.keys(params).length ? params : null }))}
                  />
                </div>
                {editable && (
                  <div className="flex flex-wrap gap-2">
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => {
                        onChange(moveRule(definition, selected, -1));
                        setSelected(Math.max(0, selected - 1));
                      }}
                      disabled={selected === 0}
                    >
                      <ArrowUp aria-hidden />
                      {t("rules.up")}
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => {
                        onChange(moveRule(definition, selected, 1));
                        setSelected(Math.min(rules.length - 1, selected + 1));
                      }}
                      disabled={selected === rules.length - 1}
                    >
                      <ArrowDown aria-hidden />
                      {t("rules.down")}
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant="destructive"
                      onClick={() => {
                        onChange(removeRule(definition, selected));
                        setSelected(rules.length > 1 ? 0 : null);
                      }}
                      data-testid="rule-remove"
                    >
                      <Trash2 aria-hidden />
                      {t("rules.remove")}
                    </Button>
                  </div>
                )}
              </div>
            )}
          </CardContent>
        </Card>
      </div>

      <FlagsCard definition={definition} editable={editable} onChange={onChange} />
      <ReviewGateCard definition={definition} editable={editable} onChange={onChange} />
      <RuleTestCard typeKey={typeKey} version={version} dirty={dirty} />
    </div>
  );
}

function FlagsCard({ definition, editable, onChange }: { definition: Definition; editable: boolean; onChange: (definition: Definition) => void }) {
  const { t } = useTranslation(REVIEW_NS);
  const flags = flagsOf(definition);
  const problems = flagIssues(definition);
  const put = (index: number, patch: Partial<FlagEntry>) => onChange(setFlags(definition, flags.map((f, i) => (i === index ? { ...f, ...patch } : f))));
  return (
    <Card data-testid="flags-card">
      <CardHeader>
        <CardTitle className="text-base">{t("rules.flags.title")}</CardTitle>
        <CardDescription>{t("rules.flags.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {flags.length === 0 && <p className="text-sm text-muted-foreground">{t("rules.flags.none")}</p>}
        {flags.map((flag, index) => (
          <div key={index} className="grid gap-2 rounded-md border p-3 sm:grid-cols-[12rem_10rem_1fr_1fr_auto]" data-testid={`flag-${flag.code}`}>
            <Input aria-label={t("rules.flags.code")} className="font-mono text-xs" value={flag.code} disabled={!editable} onChange={(e) => put(index, { code: e.target.value.toUpperCase() })} />
            <Select value={flag.severity ?? "warning"} onValueChange={(value) => put(index, { severity: value as "warning" })} disabled={!editable}>
              <SelectTrigger aria-label={t("rules.severity")}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {severities.map((s) => (
                  <SelectItem key={s} value={s}>
                    {t(`rules.severities.${s}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {(["en", "de"] as const).map((locale) => (
              <Input
                key={locale}
                aria-label={t("rules.messageIn", { locale })}
                placeholder={t("rules.messageIn", { locale })}
                value={flag.message?.[locale] ?? ""}
                disabled={!editable}
                onChange={(e) => put(index, { message: { ...(flag.message ?? {}), [locale]: e.target.value } })}
              />
            ))}
            {editable && (
              <Button type="button" size="icon-sm" variant="ghost" aria-label={t("rules.flags.remove", { code: flag.code })} onClick={() => onChange(removeFlag(definition, flag.code))}>
                <Trash2 aria-hidden />
              </Button>
            )}
            {problems.some((p) => p.code === flag.code) && (
              <p className="text-xs text-destructive sm:col-span-5" role="alert">
                {t(`rules.flags.problems.${problems.find((p) => p.code === flag.code)!.problem}`)}
              </p>
            )}
          </div>
        ))}
        {editable && (
          <div>
            <Button type="button" size="sm" variant="outline" onClick={() => onChange(setFlags(definition, [...flags, { code: newFlagCode(definition), severity: "warning", message: { en: "", de: "" } }]))} data-testid="flag-add">
              <Plus aria-hidden />
              {t("rules.flags.add")}
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function ReviewGateCard({ definition, editable, onChange }: { definition: Definition; editable: boolean; onChange: (definition: Definition) => void }) {
  const { t } = useTranslation(REVIEW_NS);
  const review = reviewOf(definition);
  const ruleIds = rulesOf(definition).map((r) => r.id);
  const put = (patch: Partial<ReviewGate>) => onChange(setReview(definition, { ...review, ...patch }));
  const toggle = (list: string[] | undefined, id: string, on: boolean) => (on ? [...(list ?? []).filter((x) => x !== id), id] : (list ?? []).filter((x) => x !== id));
  return (
    <Card data-testid="review-gate-card">
      <CardHeader>
        <CardTitle className="text-base">{t("rules.gate.title")}</CardTitle>
        <CardDescription>{t("rules.gate.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="flex flex-wrap items-center gap-6">
          <Label className="flex items-center gap-2 text-sm">
            <Switch checked={review.autoApprove === true} disabled={!editable} onCheckedChange={(on) => put({ autoApprove: on })} data-testid="gate-auto-approve" />
            {t("rules.gate.autoApprove")}
          </Label>
          <div className="grid gap-1">
            <Label className="text-xs">{t("rules.gate.priority")}</Label>
            <Select value={review.priority ?? NONE} onValueChange={(value) => put({ priority: value === NONE ? undefined : (value as "normal") })} disabled={!editable}>
              <SelectTrigger className="w-36" data-testid="gate-priority">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>{t("rules.gate.default")}</SelectItem>
                {priorities.map((p) => (
                  <SelectItem key={p} value={p}>
                    {t(`rules.gate.priorities.${p}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="grid gap-1">
            <Label className="text-xs">{t("rules.gate.dueBy")}</Label>
            <Select value={review.dueBy ?? NONE} onValueChange={(value) => put({ dueBy: value === NONE ? undefined : (value as "validFrom") })} disabled={!editable}>
              <SelectTrigger className="w-36" data-testid="gate-due-by">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>{t("rules.gate.noDue")}</SelectItem>
                {dueByRoles.map((role) => (
                  <SelectItem key={role} value={role}>
                    {role}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        </div>
        <RuleChecks title={t("rules.gate.requirePassed")} hint={t("rules.gate.requirePassedHint")} ids={ruleIds} chosen={review.requirePassedRules ?? []} disabled={!editable} testId="gate-require" onToggle={(id, on) => put({ requirePassedRules: toggle(review.requirePassedRules, id, on) })} />
        <RuleChecks title={t("rules.gate.blockOnWarnings")} hint={t("rules.gate.blockOnWarningsHint")} ids={ruleIds} chosen={review.blockOnWarnings ?? []} disabled={!editable} testId="gate-block" onToggle={(id, on) => put({ blockOnWarnings: toggle(review.blockOnWarnings, id, on) })} />
        <div className="grid gap-2">
          <h3 className="text-sm font-semibold">{t("rules.gate.lines")}</h3>
          <div className="flex flex-wrap gap-4">
            {lineSources.map((source) => (
              <Label key={source} className="flex items-center gap-2 text-sm">
                <Checkbox
                  checked={(review.lines?.requireSource ?? []).includes(source)}
                  disabled={!editable}
                  onCheckedChange={(on) => put({ lines: { ...review.lines, requireSource: toggle(review.lines?.requireSource, source, on === true) } })}
                />
                {t(`rules.gate.sources.${source}`)}
              </Label>
            ))}
          </div>
          <div className="flex flex-wrap items-center gap-6">
            <Label className="flex items-center gap-2 text-sm">
              <Switch checked={review.lines?.requireLocated === true} disabled={!editable} onCheckedChange={(on) => put({ lines: { ...review.lines, requireLocated: on } })} />
              {t("rules.gate.requireLocated")}
            </Label>
            <div className="grid gap-1">
              <Label htmlFor="gate-low-rows" className="text-xs">
                {t("rules.gate.maxLowConfidenceRows")}
              </Label>
              <Input
                id="gate-low-rows"
                className="w-28"
                inputMode="numeric"
                disabled={!editable}
                value={review.lines?.maxLowConfidenceRows ?? ""}
                onChange={(e) => {
                  const n = Number.parseInt(e.target.value, 10);
                  put({ lines: { ...review.lines, maxLowConfidenceRows: Number.isFinite(n) && n >= 0 ? n : undefined } });
                }}
              />
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

function RuleChecks({ title, hint, ids, chosen, disabled, testId, onToggle }: { title: string; hint: string; ids: string[]; chosen: string[]; disabled: boolean; testId: string; onToggle: (id: string, on: boolean) => void }) {
  return (
    <div className="grid gap-1.5" data-testid={testId}>
      <h3 className="text-sm font-semibold">{title}</h3>
      <p className="text-xs text-muted-foreground">{hint}</p>
      <div className="flex flex-wrap gap-4">
        {ids.map((id) => (
          <Label key={id} className="flex items-center gap-2 font-mono text-xs">
            <Checkbox checked={chosen.includes(id)} disabled={disabled} onCheckedChange={(on) => onToggle(id, on === true)} />
            {id}
          </Label>
        ))}
      </div>
    </div>
  );
}

const outcomeVariant: Record<string, "success" | "warning" | "destructive" | "outline"> = { passed: "success", warned: "warning", failed: "destructive", notRun: "outline" };

/** The rule test (docs/11 §19): the saved version's checks on one recent document of the type; nothing is written. */
function RuleTestCard({ typeKey, version, dirty }: { typeKey: string; version: number; dirty: boolean }) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  const { client } = useApi();
  const [documentId, setDocumentId] = useState<string>("");
  const documents = useQuery({
    queryKey: ["documents", "list", { type: typeKey, limit: 20, purpose: "rule-test" }],
    queryFn: async () => unwrap(await client.GET("/api/v1/documents", { params: { query: { type: typeKey as never, limit: 20 } } })),
  });
  const test = useMutation({
    mutationFn: async (): Promise<RuleTestResponse> =>
      unwrap(await client.POST("/api/v1/document-types/{key}/versions/{version}:test-rules", { params: { path: { key: typeKey, version } }, body: { documentId } })),
  });
  const result = test.data;
  return (
    <Card data-testid="rule-test-card">
      <CardHeader>
        <CardTitle className="text-base">{t("rules.test.title")}</CardTitle>
        <CardDescription>{t("rules.test.description", { version })}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        <div className="flex flex-wrap items-end gap-2">
          <div className="grid min-w-64 flex-1 gap-1">
            <Label className="text-xs">{t("rules.test.document")}</Label>
            <Select value={documentId} onValueChange={setDocumentId}>
              <SelectTrigger data-testid="rule-test-document">
                <SelectValue placeholder={documents.data?.items.length === 0 ? t("rules.test.noDocuments") : t("rules.test.pick")} />
              </SelectTrigger>
              <SelectContent>
                {(documents.data?.items ?? []).map((d) => (
                  <SelectItem key={d.id} value={d.id}>
                    {(d.documentNumber ?? t("rules.test.noNumber")) + " · " + (d.supplierName ?? "—") + " · " + formatDateTime(d.createdAt, i18n.language)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <Button type="button" onClick={() => test.mutate()} disabled={!documentId || dirty || test.isPending} aria-busy={test.isPending} title={dirty ? t("types.saveFirst") : undefined} data-testid="rule-test-run">
            <FlaskConical aria-hidden />
            {t("rules.test.run")}
          </Button>
        </div>
        {dirty && <p className="text-xs text-muted-foreground">{t("rules.test.saveFirst")}</p>}
        {result && (
          <div className="grid gap-2" data-testid="rule-test-result" data-blocking={result.blocking}>
            <Alert variant={result.blocking ? "destructive" : "default"}>
              <AlertDescription>{result.blocking ? t("rules.test.blocking") : t("rules.test.clean")}</AlertDescription>
            </Alert>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("rules.id")}</TableHead>
                  <TableHead>{t("rules.kind")}</TableHead>
                  <TableHead>{t("rules.test.outcome")}</TableHead>
                  <TableHead>{t("rules.test.flags")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {result.rules.map((r) => (
                  <TableRow key={r.id} data-rule={r.id} data-outcome={r.outcome}>
                    <TableCell className="font-mono text-xs">{r.id}</TableCell>
                    <TableCell className="font-mono text-xs">{r.kind}</TableCell>
                    <TableCell>
                      <Badge variant={outcomeVariant[r.outcome] ?? "outline"}>{t(`rules.test.outcomes.${r.outcome}`, { defaultValue: r.outcome })}</Badge>
                    </TableCell>
                    <TableCell className="text-xs">
                      {r.flags.map((f, i) => (
                        <p key={i}>
                          <span className="font-mono">{f.code}</span>
                          {f.field ? ` (${f.field})` : ""}: {f.message}
                        </p>
                      ))}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {result.flags.filter((f) => !result.rules.some((r) => r.flags.some((g) => g.code === f.code && g.message === f.message))).length > 0 && (
              <div className="text-xs">
                <p className="font-medium">{t("rules.test.otherFlags")}</p>
                <ul className="list-disc pl-4">
                  {result.flags
                    .filter((f) => !result.rules.some((r) => r.flags.some((g) => g.code === f.code && g.message === f.message)))
                    .map((f, i) => (
                      <li key={i}>
                        <span className="font-mono">{f.code}</span>: {f.message}
                      </li>
                    ))}
                </ul>
              </div>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
}
