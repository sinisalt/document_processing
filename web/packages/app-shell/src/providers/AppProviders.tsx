import type { ReactNode } from "react";
import { ThemeProvider } from "next-themes";
import { I18nextProvider } from "react-i18next";
import { Toaster, TooltipProvider } from "@docproc/ui";
import { AuthProvider } from "../auth/AuthProvider";
import { ApiProvider } from "../api/ApiProvider";
import { TenantSwitcherProvider } from "../api/tenantContext";
import { ErrorBoundary } from "../layout/ErrorBoundary";
import { i18n } from "../i18n";
import type { ShellConfig } from "../env";

export const THEME_STORAGE_KEY = "docproc.theme";

export interface AppProvidersProps {
  config: ShellConfig;
  children: ReactNode;
}

/**
 * Everything a screen may assume is above it: theme (next-themes, `class` strategy for the `dark` variant of
 * @docproc/ui), i18n, auth session, SuperAdmin tenant override, API client + TanStack Query, tooltips, toasts, and an
 * error boundary so a render error shows a page instead of a blank screen.
 */
export function AppProviders({ config, children }: AppProvidersProps) {
  return (
    <ThemeProvider attribute="class" defaultTheme="system" enableSystem disableTransitionOnChange storageKey={THEME_STORAGE_KEY}>
      <I18nextProvider i18n={i18n}>
        <ErrorBoundary>
          <AuthProvider config={config.auth} apiBaseUrl={config.apiBaseUrl}>
            <TenantSwitcherProvider>
              <ApiProvider baseUrl={config.apiBaseUrl}>
                <TooltipProvider delayDuration={300}>{children}</TooltipProvider>
                <Toaster richColors closeButton position="top-right" />
              </ApiProvider>
            </TenantSwitcherProvider>
          </AuthProvider>
        </ErrorBoundary>
      </I18nextProvider>
    </ThemeProvider>
  );
}
