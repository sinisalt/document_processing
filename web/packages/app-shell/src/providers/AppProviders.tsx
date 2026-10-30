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

/**
 * next-themes 0.4.6 (the latest release) always renders its anti-flash `<script>` inside the React tree. That script
 * is meant for server rendering; in this client-rendered SPA React never executes it and, in development, warns
 * "Encountered a script tag while rendering React component". Marking it a data block (`type="application/json"`,
 * through the provider's supported `scriptProps`) is what React's check skips; the theme is applied by the provider's
 * effect exactly as before, since the script never ran here.
 */
export const themeScriptProps = { type: "application/json" } as const;

export const themeProviderProps = {
  attribute: "class",
  defaultTheme: "system",
  enableSystem: true,
  disableTransitionOnChange: true,
  storageKey: THEME_STORAGE_KEY,
  scriptProps: themeScriptProps,
} as const;

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
    <ThemeProvider {...themeProviderProps}>
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
