import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { ShieldAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@docproc/ui";
import { Roles, useAuth, useTenantSwitcher } from "@docproc/app-shell";

/**
 * The tenant detail page acts *inside* the tenant: its tabs call the tenant-scoped endpoints, which need
 * `X-Tenant-Id`. The shell's TenantSwitcher owns that header, so the page points the switcher at this tenant (a
 * SuperAdmin only; the API refuses the override for anyone else) and the tabs render once the scope matches.
 */
export function useTenantScope(tenantId: string): { scoped: boolean; canScope: boolean } {
  const { hasRole } = useAuth();
  const switcher = useTenantSwitcher();
  const canScope = hasRole(Roles.SuperAdmin);
  const scoped = switcher.tenantId === tenantId;
  useEffect(() => {
    if (canScope && switcher.tenantId !== tenantId) switcher.setTenantId(tenantId);
  }, [canScope, switcher, tenantId]);
  return { scoped, canScope };
}

/** What an Operator sees on a tenant-scoped tab: the API only lets a SuperAdmin send `X-Tenant-Id`. */
export function TenantScopeNotice() {
  const { t } = useTranslation("admin");
  return (
    <Alert>
      <ShieldAlert aria-hidden />
      <AlertTitle>{t("tenant.scopeRequiredTitle")}</AlertTitle>
      <AlertDescription>{t("tenant.scopeRequired")}</AlertDescription>
    </Alert>
  );
}
