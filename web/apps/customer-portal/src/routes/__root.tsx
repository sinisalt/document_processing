import { createRootRoute } from "@tanstack/react-router";
import { AppProviders, AppShell, useAuth } from "@docproc/app-shell";
import { useTenantFeatures } from "@docproc/review";
import { navFor } from "@/nav";
import { shellConfig } from "@/shell-config";

export const Route = createRootRoute({ component: RootLayout });

/** Providers (theme, i18n, auth, API, toasts) and the authenticated layout; the matched route renders in its Outlet. */
function RootLayout() {
  return (
    <AppProviders config={shellConfig}>
      <PortalShell />
    </AppProviders>
  );
}

/** Inside the providers, so the sidebar can drop the tenant-only entries for a platform account and the entries of switches that are off. */
function PortalShell() {
  const { user } = useAuth();
  const features = useTenantFeatures(user?.scope === "tenant");
  return <AppShell nav={navFor(user, features.data)} appName={shellConfig.appName} />;
}
