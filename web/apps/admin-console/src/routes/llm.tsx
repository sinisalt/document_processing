import { useMemo } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@docproc/ui";
import { PageHeader, Policies, queryKeys, unwrap, useApi, useAuth } from "@docproc/app-shell";
import { ProviderTable } from "@/components/ProviderTable";
import { AddProviderDialog } from "@/components/AddProviderDialog";
import { UsageExplorer, platformUsageSource, type UsageSource } from "@/components/UsageExplorer";

export const Route = createFileRoute("/llm")({ component: LlmPage });

/** Platform AI providers (docs/08): the default configurations per residency with their kill switch, and spend across tenants. */
function LlmPage() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const { hasPolicy } = useAuth();
  const isPlatformAdmin = hasPolicy(Policies.PlatformAdmin);

  const providers = useQuery({
    queryKey: queryKeys.llm.platformProviders(),
    queryFn: async () => unwrap(await client.GET("/api/v1/llm/providers")),
    enabled: isPlatformAdmin,
  });
  const tenants = useQuery({
    queryKey: queryKeys.tenants.list({ limit: 200 }),
    queryFn: async () => unwrap(await client.GET("/api/v1/tenants", { params: { query: { limit: 200 } } })),
    staleTime: 60_000,
  });

  const source = useMemo<UsageSource>(() => {
    const names = new Map((tenants.data?.items ?? []).map((tenant) => [tenant.id, tenant.name] as const));
    return { ...platformUsageSource, labelFor: (key) => names.get(key), enabled: isPlatformAdmin };
  }, [tenants.data, isPlatformAdmin]);

  return (
    <>
      <PageHeader title={t("llm.title")} description={t("llm.description")} />
      <Card>
        <CardHeader>
          <CardTitle>{t("llm.platformProviders")}</CardTitle>
          <CardDescription>{t("llm.platformProvidersDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          {isPlatformAdmin ? (
            <ProviderTable
              providers={providers.data}
              isLoading={providers.isPending}
              scope="platform"
              queryKey={queryKeys.llm.platformProviders()}
              canManage
              toolbar={<AddProviderDialog scope="platform" queryKey={queryKeys.llm.platformProviders()} />}
            />
          ) : (
            <p className="text-sm text-muted-foreground">{t("llm.needsPlatformAdmin")}</p>
          )}
        </CardContent>
      </Card>
      {isPlatformAdmin && <UsageExplorer source={source} />}
    </>
  );
}
