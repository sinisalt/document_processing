export type AuthMode = "dev" | "oidc";

/** Whether a bearer token is at hand; whether the user behind it is known is `useAuth().status`. */
export type SessionStatus = "loading" | "anonymous" | "authenticated";

export interface OidcConfig {
  authority: string;
  clientId: string;
  /** Defaults to `openid profile email`. */
  scope?: string;
  /** Defaults to the app's origin + `/`. */
  redirectUri?: string;
  /** Defaults to the app's origin + `/sign-in`. */
  postLogoutRedirectUri?: string;
}

export interface ShellAuthConfig {
  mode: AuthMode;
  oidc?: OidcConfig;
}

/**
 * The token-level session the providers expose. Components use `useAuth()` instead, which adds the `/api/v1/me`
 * identity; the API client only needs `getAccessToken`.
 */
export interface AuthSession {
  mode: AuthMode;
  status: SessionStatus;
  accessToken: string | null;
  /** Stable: reads the latest token without re-rendering, for the API client middleware. */
  getAccessToken: () => string | null;
  /** The identity provider's display name when it has one (OIDC `name` claim, dev user `displayName`). */
  profileName: string | null;
  /** Dev mode takes a username; OIDC starts the redirect flow and ignores the argument. */
  signIn: (username?: string) => Promise<void>;
  signOut: () => Promise<void>;
  error: string | null;
}

/** The signed-in user as `GET /api/v1/me` describes them, plus the display name for the header. */
export interface AuthUser {
  subject: string;
  /** The API's `name`: the OIDC `preferred_username` claim. */
  name: string;
  /** What the header shows: the identity provider's full name when known, else `name`, else the e-mail. */
  displayName: string;
  email: string | null;
  roles: string[];
  /** Policies the API says this caller satisfies, in the API's order. */
  permissions: string[];
  tenantId: string | null;
  scope: "platform" | "tenant";
  authMethod: string;
}
