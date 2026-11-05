import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Ban, FlaskConical, Plus, Save, SlidersHorizontal, Trash2, Webhook } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import { ApiError, formatDateTime, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import {
  HTTP_LOOKUP,
  ParamsForm,
  PipelineSteps,
  addDisable,
  localized,
  lookupAnchors,
  lookupsOf,
  missingRequired,
  overridableSteps,
  overridesOf,
  paramFields,
  paramsText,
  putLookup,
  removeOverride,
  setParams,
  settingsWithOverrides,
  stepIdProblem,
  useDocumentTypes,
  useResolvedPipeline,
  useStepKinds,
  type JsonSchema,
  type LookupTestResponse,
  type PipelineOverride,
} from "@docproc/review";
import { useDestinations } from "@/components/integrations/api";
import type { Supplier } from "@/lib/queries";

type Editing = { mode: "disable" } | { mode: "param"; step: string } | { mode: "lookup"; id: string; after: string; params: Record<string, unknown>; editing?: string };

/**
 * The Suppliers sheet's Processing tab (docs/11 §10, §19 "Processing", P5): for one of the supplier's types, the steps
 * each phase runs for its documents (`resolved-pipeline?supplierId=`, with the layer that set each), and the supplier's
 * `pipelineOverrides`: switch a step off, set a step's parameters (the `llm` step's effort or `maxCostPerDocument`,
 * the classifier's threshold) from the kind's schema, or add an `http-lookup` after an Extract step, tried against one
 * of the supplier's documents with "Test call" (nothing is written). Saved with `PATCH /suppliers/{id} {settings}`;
 * the API checks the overrides as it stores them (fixed steps, kinds, parameters, the destination, the JSONata).
 */
export function SupplierProcessingTab({ supplier, canEdit }: { supplier: Supplier; canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const types = useDocumentTypes();
  const stepKinds = useStepKinds();
  const published = (types.data ?? []).filter((type) => type.publishedVersion != null);
  const preferred = (supplier.settings as { defaultDocumentTypeKey?: string } | null)?.defaultDocumentTypeKey;
  const [typeKey, setTypeKey] = useState<string | null>(null);
  const shownType = typeKey ?? preferred ?? published.find((type) => type.key === "Invoice")?.key ?? published[0]?.key ?? null;
  const pipeline = useResolvedPipeline(shownType, supplier.id);
  const stored = useMemo(() => overridesOf(supplier.settings), [supplier.settings]);
  const [overrides, setOverrides] = useState<PipelineOverride[]>(stored);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const dirty = JSON.stringify(overrides) !== JSON.stringify(stored);
  const steps = pipeline.data?.steps ?? [];
  const kindOf = (kind: string) => stepKinds.data?.find((k) => k.kind === kind);

  const save = useMutation({
    mutationFn: async () =>
      unwrap(
        await client.PATCH("/api/v1/suppliers/{id}", {
          params: { path: { id: supplier.id } },
          body: { name: null, vatId: null, taxId: null, ibans: null, emailDomains: null, aliases: null, defaultCurrency: null, alwaysReview: null, groupId: null, settings: settingsWithOverrides(supplier.settings, overrides) as never },
        }),
      ),
    meta: { silent: true },
    onSuccess: async (saved) => {
      setErrors([]);
      toast.success(t("processing.saved", { name: saved.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.suppliers.all });
      await queryClient.invalidateQueries({ queryKey: queryKeys.pipelines.all });
    },
    onError: (error) => {
      setErrors(error instanceof ApiError && error.isValidation ? Object.entries(error.fieldErrors).map(([k, v]) => `${k}: ${v.join(" ")}`) : [error instanceof ApiError ? (error.problem?.detail ?? error.title) : String(error)]);
    },
  });

  const describe = (o: PipelineOverride) =>
    o.op === "disable"
      ? t("processing.ops.disable", { step: o.step })
      : o.op === "param"
        ? t("processing.ops.param", { step: o.step, params: paramsText(o.params) })
        : o.op === "insertAfter"
          ? t("processing.ops.insertAfter", { id: o.with?.id ?? "", kind: o.with?.kind ?? "", step: o.step })
          : t("processing.ops.replace", { step: o.step, id: o.with?.id ?? "" });

  return (
    <div className="flex flex-col gap-4" data-testid="supplier-processing">
      <div className="flex flex-wrap items-end gap-2">
        <div className="grid gap-1">
          <Label className="text-xs">{t("processing.type")}</Label>
          <Select value={shownType ?? ""} onValueChange={setTypeKey}>
            <SelectTrigger className="w-56" data-testid="processing-type">
              <SelectValue placeholder={t("processing.pickType")} />
            </SelectTrigger>
            <SelectContent>
              {published.map((type) => (
                <SelectItem key={type.key} value={type.key}>
                  {localized(type.name, i18n.language) ?? type.key}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <p className="max-w-md text-xs text-muted-foreground">{t("processing.typeHint")}</p>
      </div>
      <PipelineSteps pipeline={pipeline.data} isLoading={pipeline.isPending && !!shownType} />

      <Card>
        <CardHeader>
          <CardTitle className="text-base">{t("processing.overridesTitle")}</CardTitle>
          <CardDescription>{t("processing.overridesDescription")}</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3">
          {overrides.length === 0 ? (
            <p className="text-sm text-muted-foreground">{t("processing.none")}</p>
          ) : (
            <ul className="grid gap-1" data-testid="processing-overrides">
              {overrides.map((o, index) => (
                <li key={index} className="flex items-center justify-between gap-2 rounded-md border px-2 py-1.5 text-sm" data-op={o.op} data-step={o.step}>
                  <span className="min-w-0 break-words">{describe(o)}</span>
                  {canEdit && (
                    <span className="flex shrink-0 gap-1">
                      {o.op === "insertAfter" && o.with?.kind === HTTP_LOOKUP && (
                        <Button type="button" size="xs" variant="ghost" onClick={() => setEditing({ mode: "lookup", id: o.with!.id, after: o.step, params: o.with!.params ?? {}, editing: o.with!.id })}>
                          {t("processing.edit")}
                        </Button>
                      )}
                      <Button type="button" size="icon-xs" variant="ghost" aria-label={t("processing.remove")} onClick={() => setOverrides(removeOverride(overrides, index))}>
                        <Trash2 aria-hidden />
                      </Button>
                    </span>
                  )}
                </li>
              ))}
            </ul>
          )}
          {canEdit && !editing && (
            <div className="flex flex-wrap gap-2">
              <Button type="button" size="sm" variant="outline" onClick={() => setEditing({ mode: "disable" })} data-testid="processing-add-disable">
                <Ban aria-hidden />
                {t("processing.addDisable")}
              </Button>
              <Button type="button" size="sm" variant="outline" onClick={() => setEditing({ mode: "param", step: "" })} data-testid="processing-add-param">
                <SlidersHorizontal aria-hidden />
                {t("processing.addParam")}
              </Button>
              <Button type="button" size="sm" variant="outline" onClick={() => setEditing({ mode: "lookup", id: "lookup", after: lookupAnchors(steps).at(-1)?.id ?? "llm", params: {} })} data-testid="processing-add-lookup">
                <Webhook aria-hidden />
                {t("processing.addLookup")}
              </Button>
            </div>
          )}
          {editing?.mode === "disable" && (
            <StepPicker
              label={t("processing.stepToDisable")}
              steps={overridableSteps(steps).filter((s) => s.enabled).map((s) => s.id)}
              onPick={(step) => {
                setOverrides(addDisable(overrides, step));
                setEditing(null);
              }}
              onCancel={() => setEditing(null)}
            />
          )}
          {editing?.mode === "param" && (
            <ParamEditor
              steps={overridableSteps(steps).filter((s) => paramFields(kindOf(s.kind)?.paramsSchema as JsonSchema).length > 0)}
              kindSchema={(kind) => kindOf(kind)?.paramsSchema as JsonSchema | undefined}
              current={(step) => (overrides.find((o) => o.op === "param" && o.step === step)?.params ?? {}) as Record<string, unknown>}
              onSave={(step, params) => {
                setOverrides(setParams(overrides, step, params));
                setEditing(null);
              }}
              onCancel={() => setEditing(null)}
            />
          )}
          {editing?.mode === "lookup" && (
            <LookupEditor
              supplierId={supplier.id}
              initial={editing}
              anchors={lookupAnchors(steps).map((s) => s.id).filter((id) => id !== editing.editing)}
              schema={kindOf(HTTP_LOOKUP)?.paramsSchema as JsonSchema | undefined}
              idProblem={(id) => stepIdProblem(id, steps, overrides, editing.editing)}
              onSave={(after, id, params) => {
                const without = editing.editing && editing.editing !== id ? overrides.filter((o) => o.with?.id !== editing.editing) : overrides;
                setOverrides(putLookup(without, after, id, params));
                setEditing(null);
              }}
              onCancel={() => setEditing(null)}
            />
          )}
          {lookupsOf(overrides).length > 0 && <p className="text-xs text-muted-foreground">{t("processing.lookupsNote")}</p>}
          {errors.length > 0 && (
            <Alert variant="destructive" data-testid="processing-errors">
              <AlertTitle>{t("processing.refused")}</AlertTitle>
              <AlertDescription>
                <ul className="list-disc pl-4">
                  {errors.map((e) => (
                    <li key={e}>{e}</li>
                  ))}
                </ul>
              </AlertDescription>
            </Alert>
          )}
          {canEdit && (
            <div className="flex gap-2">
              <Button type="button" onClick={() => save.mutate()} disabled={!dirty || save.isPending} aria-busy={save.isPending} data-testid="processing-save">
                <Save aria-hidden />
                {t("processing.save")}
              </Button>
              {dirty && (
                <Button type="button" variant="ghost" onClick={() => setOverrides(stored)}>
                  {t("processing.discard")}
                </Button>
              )}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function StepPicker({ label, steps, onPick, onCancel }: { label: string; steps: string[]; onPick: (step: string) => void; onCancel: () => void }) {
  const { t } = useTranslation();
  const [step, setStep] = useState("");
  return (
    <div className="flex flex-wrap items-end gap-2 rounded-md border p-3" data-testid="processing-step-picker">
      <div className="grid gap-1">
        <Label className="text-xs">{label}</Label>
        <Select value={step} onValueChange={setStep}>
          <SelectTrigger className="w-56" data-testid="processing-step">
            <SelectValue placeholder={t("processing.pickStep")} />
          </SelectTrigger>
          <SelectContent>
            {steps.map((id) => (
              <SelectItem key={id} value={id}>
                {id}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      <Button type="button" size="sm" disabled={!step} onClick={() => onPick(step)} data-testid="processing-step-add">
        <Plus aria-hidden />
        {t("processing.add")}
      </Button>
      <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
        {t("processing.cancel")}
      </Button>
    </div>
  );
}

function ParamEditor({
  steps,
  kindSchema,
  current,
  onSave,
  onCancel,
}: {
  steps: { id: string; kind: string }[];
  kindSchema: (kind: string) => JsonSchema | undefined;
  current: (step: string) => Record<string, unknown>;
  onSave: (step: string, params: Record<string, unknown>) => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation();
  const [step, setStep] = useState("");
  const [params, setParams] = useState<Record<string, unknown>>({});
  const kind = steps.find((s) => s.id === step)?.kind;
  return (
    <div className="grid gap-3 rounded-md border p-3" data-testid="processing-param-editor">
      <div className="grid gap-1">
        <Label className="text-xs">{t("processing.stepToChange")}</Label>
        <Select
          value={step}
          onValueChange={(value) => {
            setStep(value);
            setParams(current(value));
          }}
        >
          <SelectTrigger className="w-56" data-testid="processing-param-step">
            <SelectValue placeholder={t("processing.pickStep")} />
          </SelectTrigger>
          <SelectContent>
            {steps.map((s) => (
              <SelectItem key={s.id} value={s.id}>
                {s.id}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      {kind && <ParamsForm key={step} idPrefix="step-param" schema={kindSchema(kind)} values={params} onChange={setParams} />}
      <div className="flex gap-2">
        <Button type="button" size="sm" disabled={!step} onClick={() => onSave(step, params)} data-testid="processing-param-apply">
          {t("processing.apply")}
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
          {t("processing.cancel")}
        </Button>
      </div>
    </div>
  );
}

function LookupEditor({
  supplierId,
  initial,
  anchors,
  schema,
  idProblem,
  onSave,
  onCancel,
}: {
  supplierId: string;
  initial: { id: string; after: string; params: Record<string, unknown> };
  anchors: string[];
  schema: JsonSchema | undefined;
  idProblem: (id: string) => "form" | "taken" | null;
  onSave: (after: string, id: string, params: Record<string, unknown>) => void;
  onCancel: () => void;
}) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const destinations = useDestinations();
  const [id, setId] = useState(initial.id);
  const [after, setAfter] = useState(initial.after);
  const [params, setParamsState] = useState<Record<string, unknown>>(initial.params);
  const [documentId, setDocumentId] = useState("");
  const documents = useQuery({
    queryKey: ["documents", "list", { supplierId, limit: 20, purpose: "lookup-test" }],
    queryFn: async () => unwrap(await client.GET("/api/v1/documents", { params: { query: { supplierId, limit: 20 } } })),
  });
  const test = useMutation({
    mutationFn: async (): Promise<LookupTestResponse> => unwrap(await client.POST("/api/v1/pipelines/lookups:test", { body: { documentId, params: params as never, stepId: id } })),
  });
  const problem = idProblem(id);
  const missing = missingRequired(paramFields(schema), params);
  const httpDestinations = (destinations.data ?? []).filter((d) => d.transport === "http");
  const result = test.data;
  return (
    <div className="grid gap-3 rounded-md border p-3" data-testid="processing-lookup-editor">
      <div className="flex flex-wrap gap-3">
        <div className="grid gap-1">
          <Label htmlFor="lookup-id" className="text-xs">
            {t("processing.lookup.id")}
          </Label>
          <Input id="lookup-id" className="w-48 font-mono text-xs" value={id} onChange={(e) => setId(e.target.value)} data-testid="lookup-id" aria-invalid={problem ? true : undefined} />
          {problem && <p className="text-xs text-destructive">{t(`processing.lookup.idProblems.${problem}`)}</p>}
        </div>
        <div className="grid gap-1">
          <Label className="text-xs">{t("processing.lookup.after")}</Label>
          <Select value={after} onValueChange={setAfter}>
            <SelectTrigger className="w-48" data-testid="lookup-after">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {anchors.map((anchor) => (
                <SelectItem key={anchor} value={anchor}>
                  {anchor}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </div>
      <ParamsForm
        idPrefix="lookup-param"
        schema={schema}
        values={params}
        onChange={setParamsState}
        renderField={(field, value, set) =>
          field.name === "destinationId" ? (
            <Select value={typeof value === "string" ? value : ""} onValueChange={set}>
              <SelectTrigger data-testid="lookup-destination">
                <SelectValue placeholder={httpDestinations.length === 0 ? t("processing.lookup.noDestinations") : t("processing.lookup.pickDestination")} />
              </SelectTrigger>
              <SelectContent>
                {httpDestinations.map((d) => (
                  <SelectItem key={d.id} value={d.id}>
                    {d.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          ) : undefined
        }
      />
      {missing.length > 0 && <p className="text-xs text-muted-foreground">{t("processing.lookup.missing", { names: missing.join(", ") })}</p>}
      <div className="grid gap-2 rounded-md bg-muted/40 p-3" data-testid="lookup-test">
        <h4 className="text-sm font-semibold">{t("processing.lookup.testTitle")}</h4>
        <p className="text-xs text-muted-foreground">{t("processing.lookup.testHint")}</p>
        <div className="flex flex-wrap items-end gap-2">
          <Select value={documentId} onValueChange={setDocumentId}>
            <SelectTrigger className="w-80" data-testid="lookup-test-document">
              <SelectValue placeholder={documents.data?.items.length === 0 ? t("processing.lookup.noDocuments") : t("processing.lookup.pickDocument")} />
            </SelectTrigger>
            <SelectContent>
              {(documents.data?.items ?? []).map((d) => (
                <SelectItem key={d.id} value={d.id}>
                  {(d.documentNumber ?? t("inbox.noNumber")) + " · " + formatDateTime(d.createdAt, i18n.language)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button type="button" size="sm" variant="secondary" disabled={!documentId || missing.length > 0 || test.isPending} onClick={() => test.mutate()} data-testid="lookup-test-run">
            <FlaskConical aria-hidden />
            {t("processing.lookup.test")}
          </Button>
        </div>
        {result && (
          <div className="grid gap-1 text-sm" data-testid="lookup-test-result" data-succeeded={result.succeeded}>
            {result.succeeded ? (
              <>
                <span className="flex items-center gap-2">
                  <Badge variant="success">{t("processing.lookup.succeeded")}</Badge>
                  {result.httpStatus !== null && <span className="text-xs text-muted-foreground">HTTP {Number(result.httpStatus)}</span>}
                </span>
                <pre className="max-h-40 overflow-auto rounded bg-background p-2 font-mono text-[11px]">{JSON.stringify(result.assignments, null, 2)}</pre>
              </>
            ) : (
              <span className="flex flex-wrap items-center gap-2">
                <Badge variant="destructive">{t(`processing.lookup.reasons.${result.reason ?? "failed"}`, { defaultValue: result.reason ?? "failed" })}</Badge>
                <span>{result.message}</span>
                <span className="text-xs text-muted-foreground">{t("processing.lookup.attempts", { count: Number(result.attempts) })}</span>
              </span>
            )}
          </div>
        )}
        {test.isError && (
          <p className="text-xs text-destructive" role="alert">
            {test.error instanceof ApiError ? (test.error.isValidation ? Object.values(test.error.fieldErrors).flat().join(" ") : (test.error.problem?.detail ?? test.error.title)) : String(test.error)}
          </p>
        )}
      </div>
      <div className="flex gap-2">
        <Button type="button" size="sm" disabled={problem !== null || missing.length > 0 || !after} onClick={() => onSave(after, id, params)} data-testid="lookup-apply">
          {t("processing.apply")}
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
          {t("processing.cancel")}
        </Button>
      </div>
    </div>
  );
}
