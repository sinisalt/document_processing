import { useTranslation } from "react-i18next";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { z } from "zod";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@docproc/ui";
import { EmptyState, PageHeader, Policies, useAuth } from "@docproc/app-shell";
import { AiProvidersTab } from "@/components/settings/AiProvidersTab";
import { ApiKeysTab } from "@/components/settings/ApiKeysTab";
import { ChannelsTab } from "@/components/settings/ChannelsTab";
import { GeneralTab } from "@/components/settings/GeneralTab";
import { MembersTab } from "@/components/settings/MembersTab";

export const settingsTabs = ["general", "members", "apiKeys", "channels", "ai"] as const;
type SettingsTab = (typeof settingsTabs)[number];

export const Route = createFileRoute("/settings")({
  validateSearch: z.object({ tab: z.enum(settingsTabs).optional().catch(undefined) }),
  component: SettingsPage,
});

/** Tenant settings: General, Members, API keys, Channels, AI providers. API keys and AI providers need TenantAdmin. */
function SettingsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate({ from: Route.fullPath });
  const { tab = "general" } = Route.useSearch();
  const { hasPolicy, user } = useAuth();
  const isAdmin = hasPolicy(Policies.TenantAdmin);

  if (user?.scope !== "tenant") {
    return <EmptyState title={t("settings.platformScopeTitle")} description={t("settings.platformScope")} />;
  }
  // The sidebar hides Settings from everyone but tenant admins (docs/05 "Role gating"); a deep link gets the same answer.
  if (!isAdmin) {
    return <EmptyState title={t("settings.adminOnlyTitle")} description={t("settings.adminOnly")} />;
  }

  return (
    <>
      <PageHeader title={t("settings.title")} description={t("settings.description")} />
      <Tabs value={tab} onValueChange={(value) => void navigate({ search: { tab: value as SettingsTab } })}>
        <TabsList aria-label={t("settings.tabsLabel")} className="flex-wrap">
          <TabsTrigger value="general">{t("settings.tabs.general")}</TabsTrigger>
          <TabsTrigger value="members">{t("settings.tabs.members")}</TabsTrigger>
          {isAdmin && <TabsTrigger value="apiKeys">{t("settings.tabs.apiKeys")}</TabsTrigger>}
          <TabsTrigger value="channels">{t("settings.tabs.channels")}</TabsTrigger>
          {isAdmin && <TabsTrigger value="ai">{t("settings.tabs.ai")}</TabsTrigger>}
        </TabsList>
        <TabsContent value="general" className="pt-4">
          <GeneralTab canEdit={isAdmin} />
        </TabsContent>
        <TabsContent value="members" className="pt-4">
          <MembersTab canEdit={isAdmin} />
        </TabsContent>
        {isAdmin && (
          <TabsContent value="apiKeys" className="pt-4">
            <ApiKeysTab />
          </TabsContent>
        )}
        <TabsContent value="channels" className="pt-4">
          <ChannelsTab canEdit={isAdmin} />
        </TabsContent>
        {isAdmin && (
          <TabsContent value="ai" className="pt-4">
            <AiProvidersTab />
          </TabsContent>
        )}
      </Tabs>
    </>
  );
}
