import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Building2 } from "lucide-react";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import { useApi } from "./ApiProvider";
import { queryKeys } from "./queryKeys";
import { unwrap } from "./ApiError";
import { useTenantSwitcherOptional } from "./tenantContext";
import { useAuth } from "../auth/useAuth";
import { Roles } from "../auth/policies";

export { TenantSwitcherProvider, useTenantSwitcher, useTenantSwitcherOptional, TENANT_OVERRIDE_KEY, type TenantSwitcherContextValue } from "./tenantContext";

const PLATFORM_VALUE = "__platform__";

/**
 * The header control of the admin console: a SuperAdmin picks the tenant to act inside, or platform scope. Renders
 * nothing for everyone else (only superadmins may set `X-Tenant-Id`, docs/05 "Tenant isolation").
 */
export function TenantSwitcher({ className }: { className?: string }) {
  const { t } = useTranslation("common");
  const { hasRole, isAuthenticated } = useAuth();
  const switcher = useTenantSwitcherOptional();
  const { client } = useApi();
  const canSwitch = isAuthenticated && hasRole(Roles.SuperAdmin) && switcher !== null;

  const tenants = useQuery({
    queryKey: queryKeys.tenants.list({ limit: 200 }),
    queryFn: async () => unwrap(await client.GET("/api/v1/tenants", { params: { query: { limit: 200 } } })),
    enabled: canSwitch,
    staleTime: 60_000,
  });

  if (!canSwitch || !switcher) return null;

  const value = switcher.tenantId ?? PLATFORM_VALUE;
  return (
    <Select value={value} onValueChange={(next) => switcher.setTenantId(next === PLATFORM_VALUE ? null : next)}>
      <SelectTrigger className={className} aria-label={t("tenantSwitcher.label")} size="sm" data-testid="tenant-switcher">
        <Building2 className="size-4 text-muted-foreground" aria-hidden />
        <SelectValue placeholder={t("tenantSwitcher.platform")} />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={PLATFORM_VALUE}>{t("tenantSwitcher.platform")}</SelectItem>
        {tenants.data?.items.map((tenant) => (
          <SelectItem key={tenant.id} value={tenant.id}>
            {tenant.name}
            {tenant.status !== "Active" ? ` (${t(`tenantStatus.${tenant.status}`, { defaultValue: tenant.status })})` : ""}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
