import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts";
import { Loader2, Plus, Power, PowerOff } from "lucide-react";
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  ChartContainer,
  ChartTooltip,
  ChartTooltipContent,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  type ChartConfig,
} from "@docproc/ui";
import { ApiError, ConfirmDialog, DataTable, EmptyState, createDataTableColumnHelper, formatDate, formatMoney, formatNumber, queryKeys, relativeTime, unwrap, useApi } from "@docproc/app-shell";
import { EMPTY_ARRAY, type LlmPolicy, type LlmProvider } from "@/lib/queries";

/** `LlmProviderKind` without the test-only Replay kind; `LlmStage`; `DataResidency`. */
export const providerKinds = ["Anthropic", "AnthropicFoundry", "OpenAi", "AzureOpenAi", "OpenAiCompatible"] as const;
export const llmStages = ["Classify", "Split", "Extract", "Synthesize"] as const;
export const dataResidencies = ["Eu", "Us", "TenantOwned"] as const;

const NONE = "__none__";

const providerSchema = z.object({
  kind: z.enum(providerKinds),
  apiKey: z.string().trim().min(1),
  endpoint: z.string().trim(),
  apiVersion: z.string().trim(),
  dataResidency: z.enum(dataResidencies),
  monthlySpendCapUsd: z.string().trim(),
  models: z.object({ Classify: z.string().trim(), Split: z.string().trim(), Extract: z.string().trim(), Synthesize: z.string().trim() }),
});
type ProviderForm = z.infer<typeof providerSchema>;

const policySchema = z.object({
  defaultConfigId: z.string(),
  fallbackToPlatformKey: z.boolean(),
  stageOverrides: z.object({ Classify: z.string(), Split: z.string(), Extract: z.string(), Synthesize: z.string() }),
});
type PolicyForm = z.infer<typeof policySchema>;

function policyToForm(policy: LlmPolicy | undefined): PolicyForm {
  const overrides = { Classify: NONE, Split: NONE, Extract: NONE, Synthesize: NONE };
  for (const [stage, id] of Object.entries(policy?.stageOverrides ?? {})) {
    if (stage in overrides) overrides[stage as keyof typeof overrides] = id;
  }
  return { defaultConfigId: policy?.defaultConfigId ?? NONE, fallbackToPlatformKey: policy?.fallbackToPlatformKey ?? true, stageOverrides: overrides };
}

const helper = createDataTableColumnHelper<LlmProvider>();

/** AI providers (BYOK): the tenant's provider configurations, the per-stage policy and the usage of the last 30 days. */
export function AiProvidersTab() {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [addOpen, setAddOpen] = useState(false);
  const [toggling, setToggling] = useState<LlmProvider | null>(null);

  const providers = useQuery({ queryKey: queryKeys.llm.providers(), queryFn: async () => unwrap(await client.GET("/api/v1/llm-providers")) });
  const policy = useQuery({ queryKey: queryKeys.llm.policy(), queryFn: async () => unwrap(await client.GET("/api/v1/llm-policy")) });
  const usage = useQuery({ queryKey: queryKeys.llm.usage({ groupBy: "day" }), queryFn: async () => unwrap(await client.GET("/api/v1/llm-usage", { params: { query: { groupBy: "day" } } })) });
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: queryKeys.llm.all });

  const providerForm = useForm<ProviderForm>({
    resolver: zodResolver(providerSchema),
    defaultValues: { kind: "Anthropic", apiKey: "", endpoint: "", apiVersion: "", dataResidency: "Eu", monthlySpendCapUsd: "", models: { Classify: "", Split: "", Extract: "", Synthesize: "" } },
  });
  const create = useMutation({
    mutationFn: async (values: ProviderForm) => {
      const models = Object.fromEntries(Object.entries(values.models).filter(([, model]) => model !== ""));
      return unwrap(
        await client.POST("/api/v1/llm-providers", {
          body: {
            kind: values.kind,
            apiKey: values.apiKey,
            endpoint: values.endpoint || null,
            apiVersion: values.apiVersion || null,
            dataResidency: values.dataResidency,
            monthlySpendCapUsd: values.monthlySpendCapUsd === "" ? null : Number(values.monthlySpendCapUsd),
            models: Object.keys(models).length ? models : null,
          },
        }),
      );
    },
    onSuccess: (provider) => {
      toast.success(t("settings.ai.added", { kind: t(`settings.ai.kinds.${provider.kind}`, { defaultValue: provider.kind }) }));
      invalidate();
      providerForm.reset();
      setAddOpen(false);
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) {
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          const name = (key.charAt(0).toLowerCase() + key.slice(1)) as keyof ProviderForm;
          if (name in providerForm.getValues()) providerForm.setError(name, { type: "server", message: messages.join(" ") });
        }
      }
    },
  });
  const toggle = useMutation({
    mutationFn: async (provider: LlmProvider) =>
      provider.status === "Disabled"
        ? unwrap(await client.POST("/api/v1/llm-providers/{id}:enable", { params: { path: { id: provider.id } } }))
        : unwrap(await client.POST("/api/v1/llm-providers/{id}:disable", { params: { path: { id: provider.id } } })),
    onSuccess: (provider) => {
      toast.success(provider.status === "Disabled" ? t("settings.ai.disabled") : t("settings.ai.enabled"));
      invalidate();
    },
  });

  const policyForm = useForm<PolicyForm>({ resolver: zodResolver(policySchema), defaultValues: policyToForm(policy.data) });
  useEffect(() => {
    if (policy.data) policyForm.reset(policyToForm(policy.data));
  }, [policy.data, policyForm]);
  const savePolicy = useMutation({
    mutationFn: async (values: PolicyForm) => {
      const stageOverrides = Object.fromEntries(Object.entries(values.stageOverrides).filter(([, id]) => id !== NONE));
      return unwrap(
        await client.PUT("/api/v1/llm-policy", {
          body: { defaultConfigId: values.defaultConfigId === NONE ? null : values.defaultConfigId, fallbackToPlatformKey: values.fallbackToPlatformKey, stageOverrides: Object.keys(stageOverrides).length ? stageOverrides : null },
        }),
      );
    },
    onSuccess: (saved) => {
      queryClient.setQueryData(queryKeys.llm.policy(), saved);
      toast.success(t("settings.ai.policySaved"));
    },
  });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("kind", { header: t("settings.ai.columns.kind"), cell: (info) => <span className="font-medium">{t(`settings.ai.kinds.${info.getValue()}`, { defaultValue: info.getValue() })}</span> }),
        helper.accessor("status", {
          header: t("settings.ai.columns.status"),
          cell: (info) => (
            <Badge variant={info.getValue() === "Active" ? "default" : info.getValue() === "Invalid" ? "destructive" : "secondary"} data-status={info.getValue()}>
              {t(`settings.ai.statuses.${info.getValue()}`, { defaultValue: info.getValue() })}
            </Badge>
          ),
        }),
        helper.accessor("keyLast4", { header: t("settings.ai.columns.key"), cell: (info) => (info.getValue() ? <code className="text-xs">••••{info.getValue()}</code> : "—") }),
        helper.accessor("dataResidency", { header: t("settings.ai.columns.residency"), cell: (info) => t(`settings.ai.residencies.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor((p) => Object.entries(p.models).map(([stage, model]) => `${stage}: ${model}`).join(", "), {
          id: "models",
          header: t("settings.ai.columns.models"),
          cell: (info) => <span className="text-xs">{info.getValue() || "—"}</span>,
        }),
        helper.accessor("monthlySpendCapUsd", { header: t("settings.ai.columns.cap"), cell: (info) => (info.getValue() != null ? formatMoney(info.getValue(), "USD", i18n.language) : "—"), sortFn: "basic" }),
        helper.accessor("lastError", { header: t("settings.ai.columns.lastError"), cell: (info) => (info.getValue() ? <span className="text-destructive">{info.getValue()}</span> : "—") }),
        helper.accessor("updatedAt", { header: t("settings.ai.columns.updated"), cell: (info) => relativeTime(info.getValue(), i18n.language), sortFn: "datetime" }),
        helper.display({
          id: "actions",
          header: "",
          cell: ({ row }) => {
            const disabled = row.original.status === "Disabled";
            return (
              <Button size="xs" variant="ghost" onClick={() => setToggling(row.original)} aria-label={disabled ? t("settings.ai.enableLabel", { kind: row.original.kind }) : t("settings.ai.disableLabel", { kind: row.original.kind })}>
                {disabled ? <Power aria-hidden /> : <PowerOff aria-hidden />}
                {disabled ? t("settings.ai.enable") : t("settings.ai.disable")}
              </Button>
            );
          },
        }),
      ]),
    [t, i18n.language],
  );

  const chartConfig: ChartConfig = { calls: { label: t("settings.ai.usage.calls"), color: "var(--chart-1)" } };
  const chartData = useMemo(() => (usage.data?.buckets ?? []).map((bucket) => ({ day: bucket.key, calls: Number(bucket.calls), cost: Number(bucket.estimatedCostUsd), tokens: Number(bucket.inputTokens) + Number(bucket.outputTokens) })), [usage.data]);

  return (
    <div className="flex flex-col gap-6">
      <Card>
        <CardHeader className="flex flex-row items-start justify-between gap-4">
          <div>
            <CardTitle>{t("settings.ai.providersTitle")}</CardTitle>
            <CardDescription>{t("settings.ai.providersDescription")}</CardDescription>
          </div>
          <Dialog open={addOpen} onOpenChange={setAddOpen}>
            <Button onClick={() => setAddOpen(true)}>
              <Plus aria-hidden />
              {t("settings.ai.add")}
            </Button>
            <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-lg">
              <DialogHeader>
                <DialogTitle>{t("settings.ai.addTitle")}</DialogTitle>
                <DialogDescription>{t("settings.ai.addDescription")}</DialogDescription>
              </DialogHeader>
              <Form {...providerForm}>
                <form id="add-provider-form" onSubmit={providerForm.handleSubmit((values) => create.mutate(values))} className="flex flex-col gap-4">
                  <FormField
                    control={providerForm.control}
                    name="kind"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("settings.ai.fields.kind")}</FormLabel>
                        <Select value={field.value} onValueChange={field.onChange}>
                          <FormControl>
                            <SelectTrigger>
                              <SelectValue />
                            </SelectTrigger>
                          </FormControl>
                          <SelectContent>
                            {providerKinds.map((kind) => (
                              <SelectItem key={kind} value={kind}>
                                {t(`settings.ai.kinds.${kind}`)}
                              </SelectItem>
                            ))}
                          </SelectContent>
                        </Select>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={providerForm.control}
                    name="apiKey"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("settings.ai.fields.apiKey")}</FormLabel>
                        <FormControl>
                          <Input type="password" autoComplete="off" {...field} />
                        </FormControl>
                        <FormDescription>{t("settings.ai.fields.apiKeyHint")}</FormDescription>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                    <FormField
                      control={providerForm.control}
                      name="endpoint"
                      render={({ field }) => (
                        <FormItem>
                          <FormLabel>{t("settings.ai.fields.endpoint")}</FormLabel>
                          <FormControl>
                            <Input {...field} placeholder="https://" />
                          </FormControl>
                          <FormMessage />
                        </FormItem>
                      )}
                    />
                    <FormField
                      control={providerForm.control}
                      name="apiVersion"
                      render={({ field }) => (
                        <FormItem>
                          <FormLabel>{t("settings.ai.fields.apiVersion")}</FormLabel>
                          <FormControl>
                            <Input {...field} />
                          </FormControl>
                          <FormMessage />
                        </FormItem>
                      )}
                    />
                    <FormField
                      control={providerForm.control}
                      name="dataResidency"
                      render={({ field }) => (
                        <FormItem>
                          <FormLabel>{t("settings.ai.fields.dataResidency")}</FormLabel>
                          <Select value={field.value} onValueChange={field.onChange}>
                            <FormControl>
                              <SelectTrigger>
                                <SelectValue />
                              </SelectTrigger>
                            </FormControl>
                            <SelectContent>
                              {dataResidencies.map((value) => (
                                <SelectItem key={value} value={value}>
                                  {t(`settings.ai.residencies.${value}`)}
                                </SelectItem>
                              ))}
                            </SelectContent>
                          </Select>
                          <FormMessage />
                        </FormItem>
                      )}
                    />
                    <FormField
                      control={providerForm.control}
                      name="monthlySpendCapUsd"
                      render={({ field }) => (
                        <FormItem>
                          <FormLabel>{t("settings.ai.fields.cap")}</FormLabel>
                          <FormControl>
                            <Input type="number" min={0} step="1" {...field} />
                          </FormControl>
                          <FormMessage />
                        </FormItem>
                      )}
                    />
                  </div>
                  <fieldset className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                    <legend className="mb-1 text-sm font-medium">{t("settings.ai.fields.models")}</legend>
                    {llmStages.map((stage) => (
                      <FormField
                        key={stage}
                        control={providerForm.control}
                        name={`models.${stage}`}
                        render={({ field }) => (
                          <FormItem>
                            <FormLabel className="text-xs text-muted-foreground">{t(`stages.${stage}`)}</FormLabel>
                            <FormControl>
                              <Input {...field} placeholder={t("settings.ai.fields.modelPlaceholder")} />
                            </FormControl>
                          </FormItem>
                        )}
                      />
                    ))}
                  </fieldset>
                </form>
              </Form>
              <DialogFooter>
                <Button type="button" variant="outline" onClick={() => setAddOpen(false)}>
                  {t("common:dialog.cancel")}
                </Button>
                <Button type="submit" form="add-provider-form" disabled={create.isPending} aria-busy={create.isPending}>
                  {create.isPending && <Loader2 className="animate-spin" aria-hidden />}
                  {t("settings.ai.add")}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </CardHeader>
        <CardContent>
          <DataTable columns={columns} data={providers.data ?? EMPTY_ARRAY} getRowId={(row) => row.id} isLoading={providers.isPending} caption={t("settings.ai.caption")} emptyTitle={t("settings.ai.empty")} emptyDescription={t("settings.ai.emptyDescription")} />
        </CardContent>
      </Card>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>{t("settings.ai.policyTitle")}</CardTitle>
            <CardDescription>{t("settings.ai.policyDescription")}</CardDescription>
          </CardHeader>
          <CardContent>
            <Form {...policyForm}>
              <form onSubmit={policyForm.handleSubmit((values) => savePolicy.mutate(values))} className="flex flex-col gap-4">
                <FormField
                  control={policyForm.control}
                  name="defaultConfigId"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("settings.ai.policy.default")}</FormLabel>
                      <ProviderSelect value={field.value} onChange={field.onChange} providers={providers.data ?? []} noneLabel={t("settings.ai.policy.platformDefault")} />
                      <FormDescription>{t("settings.ai.policy.defaultHint")}</FormDescription>
                    </FormItem>
                  )}
                />
                <fieldset className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                  <legend className="mb-1 text-sm font-medium">{t("settings.ai.policy.overrides")}</legend>
                  {llmStages.map((stage) => (
                    <FormField
                      key={stage}
                      control={policyForm.control}
                      name={`stageOverrides.${stage}`}
                      render={({ field }) => (
                        <FormItem>
                          <FormLabel className="text-xs text-muted-foreground">{t(`stages.${stage}`)}</FormLabel>
                          <ProviderSelect value={field.value} onChange={field.onChange} providers={providers.data ?? []} noneLabel={t("settings.ai.policy.useDefault")} />
                        </FormItem>
                      )}
                    />
                  ))}
                </fieldset>
                <FormField
                  control={policyForm.control}
                  name="fallbackToPlatformKey"
                  render={({ field }) => (
                    <FormItem className="flex flex-row items-center justify-between rounded-lg border p-3">
                      <div className="space-y-0.5">
                        <FormLabel>{t("settings.ai.policy.fallback")}</FormLabel>
                        <FormDescription>{policy.data?.allowPlatformKeyAtAll === false ? t("settings.ai.policy.fallbackNotAllowed") : t("settings.ai.policy.fallbackHint")}</FormDescription>
                      </div>
                      <FormControl>
                        <Switch checked={field.value} onCheckedChange={field.onChange} disabled={policy.data?.allowPlatformKeyAtAll === false} aria-label={t("settings.ai.policy.fallback")} />
                      </FormControl>
                    </FormItem>
                  )}
                />
                <div className="flex justify-end">
                  <Button type="submit" disabled={savePolicy.isPending || !policyForm.formState.isDirty} aria-busy={savePolicy.isPending}>
                    {savePolicy.isPending && <Loader2 className="animate-spin" aria-hidden />}
                    {t("settings.save")}
                  </Button>
                </div>
              </form>
            </Form>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("settings.ai.usage.title")}</CardTitle>
            <CardDescription>
              {usage.data ? t("settings.ai.usage.window", { from: formatDate(usage.data.from, i18n.language), to: formatDate(usage.data.to, i18n.language) }) : t("settings.ai.usage.description")}
            </CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            {usage.data && (
              <dl className="grid grid-cols-3 gap-4 text-sm">
                <div>
                  <dt className="text-muted-foreground">{t("settings.ai.usage.calls")}</dt>
                  <dd className="text-xl font-semibold tabular-nums">{formatNumber(usage.data.total.calls, i18n.language)}</dd>
                </div>
                <div>
                  <dt className="text-muted-foreground">{t("settings.ai.usage.tokens")}</dt>
                  <dd className="text-xl font-semibold tabular-nums">{formatNumber(Number(usage.data.total.inputTokens) + Number(usage.data.total.outputTokens), i18n.language, { notation: "compact" })}</dd>
                </div>
                <div>
                  <dt className="text-muted-foreground">{t("settings.ai.usage.cost")}</dt>
                  <dd className="text-xl font-semibold tabular-nums">{formatMoney(usage.data.total.estimatedCostUsd, "USD", i18n.language)}</dd>
                </div>
              </dl>
            )}
            {chartData.length === 0 ? (
              <EmptyState title={t("settings.ai.usage.empty")} description={t("settings.ai.usage.emptyDescription")} className="py-8" />
            ) : (
              <>
                <ChartContainer config={chartConfig} className="h-56 w-full" role="img" aria-label={t("settings.ai.usage.chartLabel")}>
                  <BarChart data={chartData} accessibilityLayer barCategoryGap={2}>
                    <CartesianGrid vertical={false} strokeDasharray="3 3" />
                    <XAxis dataKey="day" tickLine={false} axisLine={false} tickMargin={8} tickFormatter={(value: string) => formatDate(value, i18n.language, { day: "numeric", month: "short" })} />
                    <YAxis allowDecimals={false} tickLine={false} axisLine={false} width={32} />
                    <ChartTooltip content={<ChartTooltipContent labelFormatter={(value) => formatDate(String(value), i18n.language)} />} />
                    <Bar dataKey="calls" fill="var(--color-calls)" radius={[4, 4, 0, 0]} maxBarSize={28} />
                  </BarChart>
                </ChartContainer>
                <details>
                  <summary className="cursor-pointer text-sm text-muted-foreground">{t("settings.ai.usage.tableToggle")}</summary>
                  <Table>
                    <TableCaption className="sr-only">{t("settings.ai.usage.chartLabel")}</TableCaption>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("settings.ai.usage.day")}</TableHead>
                        <TableHead className="text-right">{t("settings.ai.usage.calls")}</TableHead>
                        <TableHead className="text-right">{t("settings.ai.usage.tokens")}</TableHead>
                        <TableHead className="text-right">{t("settings.ai.usage.cost")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {chartData.map((row) => (
                        <TableRow key={row.day}>
                          <TableCell>{formatDate(row.day, i18n.language)}</TableCell>
                          <TableCell className="text-right tabular-nums">{formatNumber(row.calls, i18n.language)}</TableCell>
                          <TableCell className="text-right tabular-nums">{formatNumber(row.tokens, i18n.language)}</TableCell>
                          <TableCell className="text-right tabular-nums">{formatMoney(row.cost, "USD", i18n.language)}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </details>
              </>
            )}
          </CardContent>
        </Card>
      </div>

      <ConfirmDialog
        open={toggling !== null}
        onOpenChange={(open) => !open && setToggling(null)}
        title={toggling?.status === "Disabled" ? t("settings.ai.enableTitle") : t("settings.ai.disableTitle")}
        description={toggling?.status === "Disabled" ? t("settings.ai.enableDescription") : t("settings.ai.disableDescription")}
        confirmLabel={toggling?.status === "Disabled" ? t("settings.ai.enable") : t("settings.ai.disable")}
        destructive={toggling?.status !== "Disabled"}
        onConfirm={() => (toggling ? toggle.mutateAsync(toggling).then(() => undefined) : undefined)}
        isPending={toggle.isPending}
      />
    </div>
  );
}

function ProviderSelect({ value, onChange, providers, noneLabel }: { value: string; onChange: (value: string) => void; providers: LlmProvider[]; noneLabel: string }) {
  const { t } = useTranslation();
  return (
    <Select value={value} onValueChange={onChange}>
      <FormControl>
        <SelectTrigger>
          <SelectValue />
        </SelectTrigger>
      </FormControl>
      <SelectContent>
        <SelectItem value={NONE}>{noneLabel}</SelectItem>
        {providers.map((provider) => (
          <SelectItem key={provider.id} value={provider.id}>
            {t(`settings.ai.kinds.${provider.kind}`, { defaultValue: provider.kind })}
            {provider.keyLast4 ? ` ••••${provider.keyLast4}` : ""}
            {provider.status !== "Active" ? ` (${t(`settings.ai.statuses.${provider.status}`, { defaultValue: provider.status })})` : ""}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
