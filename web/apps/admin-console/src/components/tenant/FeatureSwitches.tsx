import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import type { components } from "@docproc/api-client";
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, Label, Skeleton, Switch } from "@docproc/ui";
import { isApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";

type Features = components["schemas"]["TenantFeaturesDto"];
type Settings = components["schemas"]["TenantSettingsDto"];

/**
 * The switches of docs/11 §3 (principle 6) that are built: the type catalogue (P1), outputs (P3, the Integrations
 * tab, the fan-out and delivery), source routing and supplier layouts (P4: mailboxes and watched folders polled,
 * source rules, supplier settings; layouts, identification rules and the new-layout flow); `structuredSources`
 * changes nothing yet and stays as it is.
 */
export const builtSwitches = ["configurableTypes", "customTypes", "outputs", "sourceRouting", "supplierLayouts"] as const;
export type BuiltSwitch = (typeof builtSwitches)[number];

const allOff: Features = { configurableTypes: false, customTypes: false, outputs: false, sourceRouting: false, structuredSources: false, supplierLayouts: false };

/**
 * The switches after one is flipped: `customTypes` (types of the tenant's own) needs `configurableTypes`, so switching
 * the catalogue off switches custom types off with it, and custom types on switches the catalogue on.
 */
export function withSwitch(features: Features, key: BuiltSwitch, on: boolean): Features {
  const next = { ...features, [key]: on };
  if (key === "configurableTypes" && !on) next.customTypes = false;
  if (key === "customTypes" && on) next.configurableTypes = true;
  return next;
}

/**
 * The tenant's feature switches for configurable document types and outputs (`PUT /settings` with `features`). Each
 * flip is saved at once with the rest of the settings as they are; the API answers with what it stored.
 */
export function FeatureSwitches() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const settings = useQuery({ queryKey: queryKeys.settings.get(), queryFn: async () => unwrap(await client.GET("/api/v1/settings")) });
  const current: Features = settings.data?.settings.features ?? allOff;

  const mutation = useMutation({
    mutationFn: async (features: Features) => {
      const base: Settings = settings.data!.settings;
      return unwrap(await client.PUT("/api/v1/settings", { body: { ...base, features } }));
    },
    meta: { silent: true },
    onSuccess: async (response) => {
      queryClient.setQueryData(queryKeys.settings.get(), response);
      await queryClient.invalidateQueries({ queryKey: queryKeys.documentTypes.all });
      await queryClient.invalidateQueries({ queryKey: queryKeys.outputs.all });
      toast.success(t("features.saved"));
    },
    onError: (error) => toast.error(isApiError(error) ? error.title : t("features.saveFailed")),
  });

  return (
    <Card data-testid="feature-switches">
      <CardHeader>
        <CardTitle>{t("features.title")}</CardTitle>
        <CardDescription>{t("features.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {!settings.data ? (
          <Skeleton className="h-16 w-full" aria-busy />
        ) : (
          builtSwitches.map((key) => (
            <div key={key} className="flex items-center justify-between gap-4 rounded-md border p-3">
              <div>
                <Label htmlFor={`feature-${key}`} className="flex items-center gap-2">
                  {t(`features.${key}.label`)}
                  <Badge variant="outline" className="font-mono text-[10px]">
                    {key}
                  </Badge>
                </Label>
                <p className="text-xs text-muted-foreground">{t(`features.${key}.hint`)}</p>
              </div>
              <Switch
                id={`feature-${key}`}
                checked={current[key]}
                disabled={mutation.isPending}
                onCheckedChange={(on) => mutation.mutate(withSwitch(current, key, on))}
              />
            </div>
          ))
        )}
      </CardContent>
    </Card>
  );
}
