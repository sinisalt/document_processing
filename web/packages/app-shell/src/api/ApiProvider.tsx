import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { toast } from "sonner";
import i18n from "i18next";
import { createApiClient, type ApiClient } from "@docproc/api-client";

/** openapi-fetch's `Middleware`, taken from the client so this package needs no direct dependency on it. */
type Middleware = Parameters<ApiClient["use"]>[number];
import { useAuthSession } from "../auth/AuthProvider";
import { Roles } from "../auth/policies";
import { useTenantSwitcherOptional } from "./tenantContext";
import { ApiError, errorMessage } from "./ApiError";
import { queryKeys } from "./queryKeys";

export interface ApiContextValue {
  client: ApiClient;
  queryClient: QueryClient;
  baseUrl: string;
}

const ApiContext = createContext<ApiContextValue | null>(null);

function t(key: string, fallback: string, values?: Record<string, unknown>): string {
  return i18n.isInitialized ? String(i18n.t(key, { ns: "common", defaultValue: fallback, ...values })) : fallback;
}

/**
 * How errors reach the user when a screen does not handle them itself (docs/05: problem details → toast). 401 is left to
 * the client's `onUnauthorized` (sign-out), 404 to the page that asked. A 409 (invalid transition, concurrent edit,
 * claimed task) shows the problem title and refetches what is on screen, so the user sees the state that won.
 */
function reportError(error: unknown, queryClient: QueryClient, kind: "query" | "mutation") {
  if (error instanceof ApiError) {
    if (error.status === 401) return;
    if (error.status === 404 && kind === "query") return;
    if (error.status === 409) {
      toast.warning(error.title, { description: error.problem?.detail ?? undefined });
      void queryClient.invalidateQueries({ refetchType: "active" });
      return;
    }
    if (error.status === 403) {
      toast.error(error.title, { description: error.problem?.detail ?? t("errors.forbidden", "You are not allowed to do that.") });
      return;
    }
    if (error.isValidation) {
      toast.error(error.problem?.title ?? t("errors.validation", "Please check your input."), { description: error.message });
      return;
    }
    toast.error(error.title, { description: error.problem?.detail ?? undefined });
    return;
  }
  toast.error(t("errors.requestFailed", "Request failed"), { description: errorMessage(error) });
}

export function createQueryClient(): QueryClient {
  const queryClient: QueryClient = new QueryClient({
    queryCache: new QueryCache({
      onError: (error, query) => {
        if (query.meta?.silent) return;
        reportError(error, queryClient, "query");
      },
    }),
    mutationCache: new MutationCache({
      onError: (error, _variables, _context, mutation) => {
        if (mutation.meta?.silent) return;
        reportError(error, queryClient, "mutation");
      },
    }),
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        refetchOnWindowFocus: false,
        retry: (failureCount, error) => !(error instanceof ApiError && error.status < 500) && failureCount < 2,
      },
      mutations: {
        retry: false,
      },
    },
  });
  return queryClient;
}

declare module "@tanstack/react-query" {
  interface Register {
    /** `meta: { silent: true }` on a query or mutation suppresses the global toast; the screen handles the error. */
    queryMeta: { silent?: boolean };
    mutationMeta: { silent?: boolean };
  }
}

/** The title `TenantResolutionMiddleware` answers a non-SuperAdmin's `X-Tenant-Id` with (403). */
export const TENANT_OVERRIDE_REFUSED_TITLE = "Tenant override not allowed";

async function isOverrideRefusal(response: Response): Promise<boolean> {
  try {
    const problem: unknown = await response.clone().json();
    return !!problem && typeof problem === "object" && (problem as { title?: unknown }).title === TENANT_OVERRIDE_REFUSED_TITLE;
  } catch {
    return false;
  }
}

/**
 * Whether the tenant override may go out: only while the signed-in user is known to be a SuperAdmin. The API answers
 * every other role's `X-Tenant-Id` with 403 "Tenant override not allowed" (even on `/me`), so a stale override must never
 * reach another user. Before `/me` has answered the roles are unknown; the override is cleared on sign-out, so what is
 * left then is the SuperAdmin's own choice from this tab.
 */
export function mayOverrideTenant(me: unknown): boolean {
  if (!me || typeof me !== "object") return true;
  const roles = (me as { roles?: unknown }).roles;
  return Array.isArray(roles) && roles.includes(Roles.SuperAdmin);
}

export interface ApiProviderProps {
  /** API origin; empty for same origin (the Vite dev proxy forwards `/api`). */
  baseUrl?: string;
  children: ReactNode;
}

/**
 * The one `@docproc/api-client` of the app, with the bearer token from the auth session, the SuperAdmin tenant
 * override as `X-Tenant-Id`, and a `QueryClient` whose global error handling toasts problem details.
 */
export function ApiProvider({ baseUrl = "", children }: ApiProviderProps) {
  const session = useAuthSession();
  const switcher = useTenantSwitcherOptional();
  const tenantId = switcher?.tenantId ?? null;

  const tenantRef = useRef<string | null>(tenantId);
  tenantRef.current = tenantId;
  const setTenantRef = useRef(switcher?.setTenantId);
  setTenantRef.current = switcher?.setTenantId;
  const signOutRef = useRef(session.signOut);
  signOutRef.current = session.signOut;

  const [queryClient] = useState(createQueryClient);

  const client = useMemo(() => {
    const created = createApiClient({
      baseUrl,
      getAccessToken: session.getAccessToken,
      onUnauthorized: () => {
        // The token the server rejected is useless; drop it so the shell shows the sign-in page.
        if (session.getAccessToken()) void signOutRef.current();
      },
    });
    const tenantOverride: Middleware = {
      onRequest({ request }) {
        const override = tenantRef.current;
        if (override && mayOverrideTenant(queryClient.getQueryData(queryKeys.me()))) request.headers.set("X-Tenant-Id", override);
        else request.headers.delete("X-Tenant-Id");
        return request;
      },
      async onResponse({ request, response }) {
        // The API refuses X-Tenant-Id from anyone but a SuperAdmin. Should an override still reach such a user (a role
        // revoked mid-session), drop it, so the shell's next requests (and `/me`) go out without it instead of the
        // user staying locked out with no switcher to clear it.
        if (response.status === 403 && request.headers.has("X-Tenant-Id") && (await isOverrideRefusal(response))) {
          setTenantRef.current?.(null);
        }
        return response;
      },
    };
    created.use(tenantOverride);
    return created;
  }, [baseUrl, session.getAccessToken, queryClient]);

  // Sign-out: everything cached belonged to that user. Nothing authenticated is in flight by then (the layout is
  // gone), so dropping the cache is safe. A fresh sign-in must *not* clear: `/me` is already fetching and
  // `clear()` would destroy that query under its observer, leaving the shell on the loading screen.
  const hasToken = session.accessToken !== null;
  const hadToken = useRef(hasToken);
  useEffect(() => {
    if (hadToken.current && !hasToken) queryClient.clear();
    hadToken.current = hasToken;
  }, [hasToken, queryClient]);

  // The tenant override is the signed-out SuperAdmin's choice, not the next user's: drop it whenever nobody is signed
  // in (sign-out, an expired token, a tab that reopens without one). "loading" (OIDC restoring its user) keeps it.
  const anonymous = session.status === "anonymous";
  const clearOverride = switcher?.setTenantId;
  useEffect(() => {
    if (anonymous && tenantRef.current !== null) clearOverride?.(null);
  }, [anonymous, clearOverride]);

  // Tenant override: every tenant-scoped query now means something else. Reset (not clear) so active queries refetch
  // under the new `X-Tenant-Id` while their observers stay attached.
  const previousTenant = useRef(tenantId);
  useEffect(() => {
    if (previousTenant.current !== tenantId) {
      previousTenant.current = tenantId;
      void queryClient.resetQueries();
    }
  }, [tenantId, queryClient]);

  const value = useMemo<ApiContextValue>(() => ({ client, queryClient, baseUrl }), [client, queryClient, baseUrl]);

  return (
    <ApiContext.Provider value={value}>
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    </ApiContext.Provider>
  );
}

/** The typed API client (`client.GET("/api/v1/documents", { params: { query } })`) and the app's query client. */
export function useApi(): ApiContextValue {
  const value = useContext(ApiContext);
  if (!value) throw new Error("useApi must be used inside <ApiProvider>.");
  return value;
}
