import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { UserManager, WebStorageStateStore, type User } from "oidc-client-ts";
import { AuthProvider as OidcProvider, useAuth as useOidc } from "react-oidc-context";
import { issueDevToken } from "./devAuthApi";
import { createTokenStore, safeSessionStorage, type StoredToken } from "./tokenStore";
import type { AuthSession, OidcConfig, ShellAuthConfig } from "./types";

const AuthSessionContext = createContext<AuthSession | null>(null);

export interface AuthProviderProps {
  config: ShellAuthConfig;
  /** Origin of the API for the `/dev/*` endpoints; empty for same origin (the Vite proxy). */
  apiBaseUrl?: string;
  children: ReactNode;
}

/**
 * Picks the identity provider from `VITE_AUTH_MODE`: `dev` signs in through the API's own token issuer (docs/09),
 * `oidc` through the configured OpenID Connect authority with the redirect flow and silent renew. Both end in the same
 * `AuthSession` so nothing else in the apps knows the difference.
 */
export function AuthProvider({ config, apiBaseUrl = "", children }: AuthProviderProps) {
  if (config.mode === "oidc") {
    if (!config.oidc) {
      throw new Error("VITE_AUTH_MODE=oidc needs VITE_OIDC_AUTHORITY and VITE_OIDC_CLIENT_ID.");
    }
    return <OidcAuthProvider config={config.oidc}>{children}</OidcAuthProvider>;
  }
  return <DevAuthProvider apiBaseUrl={apiBaseUrl}>{children}</DevAuthProvider>;
}

/** The token-level session; most components want `useAuth()` instead. */
export function useAuthSession(): AuthSession {
  const session = useContext(AuthSessionContext);
  if (!session) throw new Error("useAuthSession must be used inside <AuthProvider>.");
  return session;
}

// ---------------------------------------------------------------------------------------------------------------------
// dev: POST /dev/token, token in sessionStorage with its expiry

function DevAuthProvider({ apiBaseUrl, children }: { apiBaseUrl: string; children: ReactNode }) {
  const store = useMemo(() => createTokenStore(safeSessionStorage()), []);
  const [token, setToken] = useState<StoredToken | null>(() => store.load());
  const [error, setError] = useState<string | null>(null);
  const tokenRef = useRef<string | null>(token?.accessToken ?? null);
  tokenRef.current = token?.accessToken ?? null;

  // Drop the token the moment it expires so the next render shows the sign-in page instead of a wall of 401s.
  useEffect(() => {
    if (!token) return;
    const remaining = store.remainingMs();
    const handle = window.setTimeout(() => {
      store.clear();
      setToken(null);
    }, remaining);
    return () => window.clearTimeout(handle);
  }, [token, store]);

  const getAccessToken = useCallback(() => tokenRef.current, []);

  const signIn = useCallback(
    async (username?: string) => {
      if (!username) throw new Error("Dev sign-in needs a username.");
      setError(null);
      try {
        const issued = await issueDevToken(username, apiBaseUrl);
        const saved = store.save({
          accessToken: issued.access_token,
          expiresIn: issued.expires_in,
          username: issued.user.username,
          displayName: issued.user.displayName,
        });
        tokenRef.current = saved.accessToken;
        setToken(saved);
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
        throw e;
      }
    },
    [apiBaseUrl, store],
  );

  const signOut = useCallback(async () => {
    store.clear();
    tokenRef.current = null;
    setToken(null);
  }, [store]);

  const session = useMemo<AuthSession>(
    () => ({
      mode: "dev",
      status: token ? "authenticated" : "anonymous",
      accessToken: token?.accessToken ?? null,
      getAccessToken,
      profileName: token?.displayName ?? null,
      signIn,
      signOut,
      error,
    }),
    [token, getAccessToken, signIn, signOut, error],
  );

  return <AuthSessionContext.Provider value={session}>{children}</AuthSessionContext.Provider>;
}

// ---------------------------------------------------------------------------------------------------------------------
// oidc: react-oidc-context + oidc-client-ts, authorization code + PKCE, silent renew

export function createUserManager(config: OidcConfig): UserManager {
  const origin = window.location.origin;
  return new UserManager({
    authority: config.authority,
    client_id: config.clientId,
    redirect_uri: config.redirectUri ?? `${origin}/`,
    post_logout_redirect_uri: config.postLogoutRedirectUri ?? `${origin}/sign-in`,
    scope: config.scope ?? "openid profile email",
    response_type: "code",
    automaticSilentRenew: true,
    loadUserInfo: false,
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  });
}

/** Removes `code`/`state` from the address bar after the redirect back, keeping the path the user wanted. */
function stripAuthParams(_user: User | undefined) {
  const url = new URL(window.location.href);
  for (const key of ["code", "state", "session_state", "iss"]) url.searchParams.delete(key);
  window.history.replaceState({}, document.title, url.pathname + url.search + url.hash);
}

function OidcAuthProvider({ config, children }: { config: OidcConfig; children: ReactNode }) {
  const userManager = useMemo(() => createUserManager(config), [config]);
  return (
    <OidcProvider userManager={userManager} onSigninCallback={stripAuthParams}>
      <OidcSessionBridge>{children}</OidcSessionBridge>
    </OidcProvider>
  );
}

function OidcSessionBridge({ children }: { children: ReactNode }) {
  const oidc = useOidc();
  const tokenRef = useRef<string | null>(null);
  tokenRef.current = oidc.user?.access_token ?? null;
  const getAccessToken = useCallback(() => tokenRef.current, []);

  const signIn = useCallback(async () => {
    await oidc.signinRedirect({ state: { returnTo: window.location.pathname + window.location.search } });
  }, [oidc]);

  const signOut = useCallback(async () => {
    await oidc.removeUser();
    try {
      await oidc.signoutRedirect();
    } catch {
      // No end_session_endpoint at the authority: the local session is gone, which is what matters.
    }
  }, [oidc]);

  const busy = oidc.isLoading || oidc.activeNavigator !== undefined;
  const session = useMemo<AuthSession>(
    () => ({
      mode: "oidc",
      status: busy ? "loading" : oidc.isAuthenticated && oidc.user ? "authenticated" : "anonymous",
      accessToken: oidc.user?.access_token ?? null,
      getAccessToken,
      profileName: oidc.user?.profile.name ?? null,
      signIn,
      signOut,
      error: oidc.error?.message ?? null,
    }),
    [busy, oidc.isAuthenticated, oidc.user, oidc.error, getAccessToken, signIn, signOut],
  );

  return <AuthSessionContext.Provider value={session}>{children}</AuthSessionContext.Provider>;
}
