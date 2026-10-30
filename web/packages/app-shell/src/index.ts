// @docproc/app-shell: everything both apps share above the screen level (docs/05 "Web implementation").

// auth
export { AuthProvider, useAuthSession, createUserManager, type AuthProviderProps } from "./auth/AuthProvider";
export { useAuth, toAuthUser, type AuthStatus, type UseAuthResult } from "./auth/useAuth";
export * from "./auth/policies";
export * from "./auth/types";
export { createTokenStore, safeSessionStorage, DEFAULT_TOKEN_KEY, EXPIRY_SKEW_MS, type StoredToken, type TokenStore, type SaveTokenInput } from "./auth/tokenStore";
export { listDevUsers, issueDevToken, DevAuthError, type DevUser, type DevTokenResponse } from "./auth/devAuthApi";

// api
export { ApiProvider, useApi, createQueryClient, type ApiContextValue, type ApiProviderProps } from "./api/ApiProvider";
export { ApiError, isApiError, unwrap, errorMessage, type FetchResult } from "./api/ApiError";
export * from "./api/queryKeys";
export { useCursorList, type CursorPage, type UseCursorListOptions, type UseCursorListResult } from "./api/useCursorList";
export {
  TenantSwitcher,
  TenantSwitcherProvider,
  useTenantSwitcher,
  useTenantSwitcherOptional,
  TENANT_OVERRIDE_KEY,
  type TenantSwitcherContextValue,
} from "./api/TenantSwitcher";

// layout
export { AppShell, Redirect, safeRedirect, REDIRECT_PARAM, type AppShellProps } from "./layout/AppShell";
export { AppLayout, type AppLayoutProps } from "./layout/AppLayout";
export { UserMenu } from "./layout/UserMenu";
export { SignInPage, type SignInPageProps } from "./layout/SignInPage";
export { ErrorBoundary, ErrorPage, RouteErrorPage, type ErrorBoundaryProps, type ErrorPageProps } from "./layout/ErrorBoundary";
export { NotFoundPage } from "./layout/NotFoundPage";
export { LoadingScreen } from "./layout/LoadingScreen";
export * from "./layout/nav";

// components
export { DataTable, dataTableFeatures, createDataTableColumnHelper, type DataTableProps, type DataTableColumns, type DataTableFeatures } from "./components/DataTable";
export { ConfirmDialog, type ConfirmDialogProps } from "./components/ConfirmDialog";
export { EmptyState, type EmptyStateProps } from "./components/EmptyState";
export { PageHeader, type PageHeaderProps } from "./components/PageHeader";
export * from "./components/StatusBadge";
export { FieldStatusBadge, ConfidenceBadge, SourceBadge, type ConfidenceBadgeProps } from "./components/FieldBadges";

// utilities
export * from "./lib/format";
export * from "./lib/fieldPath";
export * from "./lib/fieldValue";

// i18n, config, providers
export { createI18n, i18n, supportedLanguages, isSupportedLanguage, currentLanguage, commonResources, LANGUAGE_STORAGE_KEY, type SupportedLanguage, type CreateI18nOptions } from "./i18n";
export { readShellConfig, type ShellConfig } from "./env";
export { AppProviders, THEME_STORAGE_KEY, type AppProvidersProps } from "./providers/AppProviders";
