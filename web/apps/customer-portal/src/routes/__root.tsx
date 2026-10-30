import { createRootRoute } from "@tanstack/react-router";
import { AppProviders, AppShell } from "@docproc/app-shell";
import { nav } from "@/nav";
import { shellConfig } from "@/shell-config";

export const Route = createRootRoute({ component: RootLayout });

/** Providers (theme, i18n, auth, API, toasts) and the authenticated layout; the matched route renders in its Outlet. */
function RootLayout() {
  return (
    <AppProviders config={shellConfig}>
      <AppShell nav={nav} appName={shellConfig.appName} />
    </AppProviders>
  );
}
