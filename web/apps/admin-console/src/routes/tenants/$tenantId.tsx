import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { ArrowLeft } from "lucide-react";
import { Button, Skeleton, Tabs, TabsContent, TabsList, TabsTrigger } from "@docproc/ui";
import { ApiError, EmptyState, PageHeader, TenantStatusBadge, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { OverviewTab } from "@/components/tenant/OverviewTab";
import { ChannelsTab } from "@/components/tenant/ChannelsTab";
import { MembersTab } from "@/components/tenant/MembersTab";
import { SuppliersTab } from "@/components/tenant/SuppliersTab";
import { SettingsTab } from "@/components/tenant/SettingsTab";
import { ApiKeysTab } from "@/components/tenant/ApiKeysTab";
import { LlmTab } from "@/components/tenant/LlmTab";
import { TenantScopeNotice, useTenantScope } from "@/components/tenant/TenantScope";

export const tenantTabs = ["overview", "channels", "members", "suppliers", "settings", "api-keys", "llm"] as const;
type TenantTab = (typeof tenantTabs)[number];

const searchSchema = z.object({ tab: z.enum(tenantTabs).optional().catch(undefined) });

export const Route = createFileRoute("/tenants/$tenantId")({ validateSearch: searchSchema, component: TenantDetailPage });

/** The tenant detail: identity from `GET /tenants/{id}`; every other tab acts inside the tenant through the switcher. */
function TenantDetailPage() {
  const { t } = useTranslation("admin");
  const { tenantId } = Route.useParams();
  const { tab = "overview" } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { client } = useApi();
  const { scoped, canScope } = useTenantScope(tenantId);

  const tenant = useQuery({
    queryKey: queryKeys.tenants.detail(tenantId),
    queryFn: async () => unwrap(await client.GET("/api/v1/tenants/{id}", { params: { path: { id: tenantId } } })),
    retry: false,
  });

  if (tenant.isError && tenant.error instanceof ApiError && tenant.error.status === 404) {
    return (
      <EmptyState
        title={t("tenant.notFound")}
        description={tenantId}
        action={
          <Button variant="outline" asChild>
            <Link to="/tenants">{t("tenant.backToList")}</Link>
          </Button>
        }
      />
    );
  }

  const scopedTab = (content: React.ReactNode) => (scoped ? content : canScope ? <Skeleton className="h-40 w-full" aria-busy /> : <TenantScopeNotice />);

  return (
    <>
      <PageHeader
        title={
          tenant.data ? (
            <span className="flex flex-wrap items-center gap-2">
              {tenant.data.name}
              <TenantStatusBadge status={tenant.data.status} />
            </span>
          ) : (
            <Skeleton className="h-8 w-48" />
          )
        }
        // The legal name only when it differs from the title ("Gasthaus zur Post GmbH · Gasthaus zur Post GmbH" said nothing).
        description={tenant.data ? [tenant.data.legalName !== tenant.data.name ? tenant.data.legalName : null, tenant.data.slug, tenant.data.countryCode].filter(Boolean).join(" · ") : undefined}
        actions={
          <Button variant="ghost" size="sm" asChild>
            <Link to="/tenants">
              <ArrowLeft aria-hidden />
              {t("tenant.backToList")}
            </Link>
          </Button>
        }
      />
      <Tabs value={tab} onValueChange={(value) => void navigate({ search: { tab: value as TenantTab }, replace: true })}>
        <TabsList className="flex-wrap">
          {tenantTabs.map((value) => (
            <TabsTrigger key={value} value={value}>
              {t(`tenant.tabs.${value}`)}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="overview" className="pt-4">
          {tenant.data ? <OverviewTab tenant={tenant.data} /> : <Skeleton className="h-40 w-full" aria-busy />}
        </TabsContent>
        <TabsContent value="channels" className="pt-4">
          {scopedTab(<ChannelsTab />)}
        </TabsContent>
        <TabsContent value="members" className="pt-4">
          {scopedTab(<MembersTab />)}
        </TabsContent>
        <TabsContent value="suppliers" className="pt-4">
          {scopedTab(<SuppliersTab />)}
        </TabsContent>
        <TabsContent value="settings" className="pt-4">
          {scopedTab(<SettingsTab />)}
        </TabsContent>
        <TabsContent value="api-keys" className="pt-4">
          {scopedTab(<ApiKeysTab />)}
        </TabsContent>
        <TabsContent value="llm" className="pt-4">
          {scopedTab(<LlmTab />)}
        </TabsContent>
      </Tabs>
    </>
  );
}
