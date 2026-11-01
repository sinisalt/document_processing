import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import type { components } from "@docproc/api-client";
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Skeleton, Switch } from "@docproc/ui";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { AddProviderDialog, llmStages } from "../AddProviderDialog";
import { ProviderTable, type LlmProvider } from "../ProviderTable";
import { UsageExplorer, tenantUsageSource } from "../UsageExplorer";

type Policy = components["schemas"]["LlmPolicyResponse"];

const NONE = "__none__";

function PolicyForm({ policy, providers }: { policy: Policy; providers: LlmProvider[] }) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [defaultConfigId, setDefaultConfigId] = useState<string | null>(policy.defaultConfigId);
  const [fallback, setFallback] = useState(policy.fallbackToPlatformKey);
  const [overrides, setOverrides] = useState<Record<string, string>>(policy.stageOverrides);
  const dirty = defaultConfigId !== policy.defaultConfigId || fallback !== policy.fallbackToPlatformKey || JSON.stringify(overrides) !== JSON.stringify(policy.stageOverrides);

  const mutation = useMutation({
    mutationFn: async () => unwrap(await client.PUT("/api/v1/llm-policy", { body: { defaultConfigId, fallbackToPlatformKey: fallback, stageOverrides: overrides } })),
    onSuccess: async () => {
      toast.success(t("llm.policySaved"));
      await queryClient.invalidateQueries({ queryKey: queryKeys.llm.policy() });
    },
  });

  const providerLabel = (p: LlmProvider) => `${p.kind} · ${t(`llm.residency.${p.dataResidency}`, { defaultValue: p.dataResidency })}${p.keyLast4 ? ` · ••••${p.keyLast4}` : ""}`;
  const providerSelect = (id: string, value: string | null, onChange: (next: string | null) => void, label: string) => (
    <Select value={value ?? NONE} onValueChange={(next) => onChange(next === NONE ? null : next)}>
      <SelectTrigger id={id} className="w-full" aria-label={label}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={NONE}>{t("llm.policyNone")}</SelectItem>
        {providers.map((p) => (
          <SelectItem key={p.id} value={p.id}>
            {providerLabel(p)}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );

  return (
    <div className="grid gap-5">
      <div className="grid gap-2">
        <Label htmlFor="policy-default">{t("llm.policyDefault")}</Label>
        {providerSelect("policy-default", defaultConfigId, setDefaultConfigId, t("llm.policyDefault"))}
        {/* Hints at the Settings tab's FormDescription size (text-sm), so the two tenant forms read as one style. */}
        <p className="text-sm text-muted-foreground">{t("llm.policyDefaultHint")}</p>
      </div>
      <div className="flex items-center justify-between gap-4 rounded-md border p-3">
        <div>
          <Label htmlFor="policy-fallback">{t("llm.policyFallback")}</Label>
          <p id="policy-fallback-hint" className="text-sm text-muted-foreground">
            {t("llm.policyFallbackHint")}{" "}
            {/* The switch shows an allowed fallback; only the platform's veto, which also disables it, needs saying. */}
            {!policy.allowPlatformKeyAtAll && <Badge variant="destructive">{t("llm.platformKeyForbidden")}</Badge>}
          </p>
        </div>
        <Switch id="policy-fallback" aria-describedby="policy-fallback-hint" checked={fallback} onCheckedChange={setFallback} disabled={!policy.allowPlatformKeyAtAll} />
      </div>
      <fieldset className="grid gap-3 rounded-md border p-3">
        <legend className="px-1 text-sm font-medium">{t("llm.policyOverrides")}</legend>
        <div className="grid gap-3 sm:grid-cols-2">
          {llmStages.map((stage) => (
            <div key={stage} className="grid gap-1">
              <Label htmlFor={`policy-stage-${stage}`}>{t(`stages.${stage}`)}</Label>
              {providerSelect(
                `policy-stage-${stage}`,
                overrides[stage] ?? null,
                (next) =>
                  setOverrides((current) => {
                    const copy = { ...current };
                    if (next) copy[stage] = next;
                    else delete copy[stage];
                    return copy;
                  }),
                t("llm.policyOverrideFor", { stage: t(`stages.${stage}`) }),
              )}
            </div>
          ))}
        </div>
      </fieldset>
      <div className="flex justify-end">
        <Button onClick={() => mutation.mutate()} disabled={!dirty || mutation.isPending} aria-busy={mutation.isPending}>
          {t("llm.savePolicy")}
        </Button>
      </div>
    </div>
  );
}

/** The tenant's own providers (BYOK), its routing policy and its usage by day/stage/model (docs/08). */
export function LlmTab() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const providers = useQuery({ queryKey: queryKeys.llm.providers(), queryFn: async () => unwrap(await client.GET("/api/v1/llm-providers")) });
  const policy = useQuery({ queryKey: queryKeys.llm.policy(), queryFn: async () => unwrap(await client.GET("/api/v1/llm-policy")) });

  return (
    <div className="grid gap-6">
      <Card>
        <CardHeader>
          <CardTitle>{t("llm.tenantProviders")}</CardTitle>
          <CardDescription>{t("llm.tenantProvidersDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          <ProviderTable providers={providers.data} isLoading={providers.isPending} query={providers} scope="tenant" queryKey={queryKeys.llm.providers()} canManage actions={<AddProviderDialog scope="tenant" queryKey={queryKeys.llm.providers()} />} />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>{t("llm.policy")}</CardTitle>
          <CardDescription>{t("llm.policyDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          {policy.data && providers.data ? (
            <PolicyForm key={policy.dataUpdatedAt} policy={policy.data} providers={providers.data} />
          ) : (
            <div className="grid gap-3" aria-busy>
              <Skeleton className="h-9 w-full" />
              <Skeleton className="h-16 w-full" />
            </div>
          )}
        </CardContent>
      </Card>
      <UsageExplorer source={tenantUsageSource} title={t("llm.tenantUsage")} description={t("llm.tenantUsageDescription")} />
    </div>
  );
}
