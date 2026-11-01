import { useCallback, useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import type { components } from "@docproc/api-client";
import { useApi } from "../api/ApiProvider";
import { queryKeys } from "../api/queryKeys";
import { ApiError, unwrap } from "../api/ApiError";
import { useAuthSession } from "./AuthProvider";
import { hasPolicy as rolesSatisfy, hasRole as rolesInclude, type Policy, type Role } from "./policies";
import type { AuthMode, AuthUser } from "./types";

type MeResponse = components["schemas"]["MeResponse"];

export type AuthStatus = "loading" | "anonymous" | "authenticated" | "error";

export interface UseAuthResult {
  mode: AuthMode;
  status: AuthStatus;
  isAuthenticated: boolean;
  accessToken: string | null;
  /** Present once `GET /api/v1/me` answered. */
  user: AuthUser | null;
  error: string | null;
  signIn: (username?: string) => Promise<void>;
  signOut: () => Promise<void>;
  hasRole: (...roles: Role[]) => boolean;
  /** Prefers the API's `permissions`; falls back to the role table while `/me` is still loading. */
  hasPolicy: (policy: Policy) => boolean;
  refetchUser: () => Promise<unknown>;
}

export function toAuthUser(me: MeResponse, profileName: string | null): AuthUser {
  const name = me.name ?? me.subject;
  return {
    subject: me.subject,
    name,
    displayName: profileName ?? name ?? me.email ?? me.subject,
    email: me.email ?? null,
    roles: me.roles,
    permissions: me.permissions,
    tenantId: me.tenantId ?? null,
    scope: me.scope === "platform" ? "platform" : "tenant",
    authMethod: me.authMethod,
  };
}

/** The signed-in user: the token session plus the identity `GET /api/v1/me` reports for it. */
export function useAuth(): UseAuthResult {
  const session = useAuthSession();
  const { client } = useApi();
  const hasToken = session.status === "authenticated";

  const me = useQuery({
    queryKey: queryKeys.me(),
    queryFn: async () => unwrap(await client.GET("/api/v1/me")),
    enabled: hasToken,
    staleTime: 5 * 60_000,
    retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
  });

  const user = useMemo(() => (me.data ? toAuthUser(me.data, session.profileName) : null), [me.data, session.profileName]);

  const status: AuthStatus =
    session.status === "loading" ? "loading" : !hasToken ? "anonymous" : me.data ? "authenticated" : me.isError ? "error" : "loading";

  const roles = user?.roles;
  const permissions = user?.permissions;
  const hasRole = useCallback((...wanted: Role[]) => rolesInclude(roles, ...wanted), [roles]);
  const hasPolicy = useCallback(
    (policy: Policy) => (permissions ? permissions.includes(policy) : rolesSatisfy(roles, policy)),
    [permissions, roles],
  );

  return {
    mode: session.mode,
    status,
    isAuthenticated: status === "authenticated",
    accessToken: session.accessToken,
    user,
    error: session.error ?? (me.error ? (me.error instanceof Error ? me.error.message : String(me.error)) : null),
    signIn: session.signIn,
    signOut: session.signOut,
    hasRole,
    hasPolicy,
    refetchUser: me.refetch,
  };
}
