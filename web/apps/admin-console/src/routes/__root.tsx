import { createRootRoute } from "@tanstack/react-router";
import { AppProviders, AppShell } from "@docproc/app-shell";
import { nav } from "@/nav";
import { shellConfig } from "@/shell-config";

export const Route = createRootRoute({ component: RootLayout });

/** Providers and the authenticated layout; SuperAdmins get the tenant switcher in the header (X-Tenant-Id). */
function RootLayout() {
  return (
    <AppProviders config={shellConfig}>
      <AppShell nav={nav} appName={shellConfig.appName} showTenantSwitcher />
    </AppProviders>
  );
}
