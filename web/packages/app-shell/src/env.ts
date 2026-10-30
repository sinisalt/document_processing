import type { ShellAuthConfig } from "./auth/types";

export interface ShellConfig {
  auth: ShellAuthConfig;
  /** API origin for the client and the dev endpoints; empty for same origin (Vite proxies `/api` and `/dev`). */
  apiBaseUrl: string;
  /** Shown in the sidebar header and the browser title. */
  appName: string;
}

type Env = Record<string, string | boolean | undefined>;

function str(env: Env, key: string): string | undefined {
  const value = env[key];
  return typeof value === "string" && value.trim() !== "" ? value.trim() : undefined;
}

/**
 * Reads the shell's settings from `import.meta.env` (`.env.development` sets `VITE_AUTH_MODE=dev`, `.env.production`
 * `oidc`). Kept out of the components so the apps decide where configuration comes from.
 */
export function readShellConfig(env: Env, defaults: { appName: string }): ShellConfig {
  const mode = str(env, "VITE_AUTH_MODE") === "oidc" ? "oidc" : "dev";
  const authority = str(env, "VITE_OIDC_AUTHORITY");
  const clientId = str(env, "VITE_OIDC_CLIENT_ID");
  return {
    auth: {
      mode,
      ...(authority && clientId
        ? {
            oidc: {
              authority,
              clientId,
              scope: str(env, "VITE_OIDC_SCOPE"),
              redirectUri: str(env, "VITE_OIDC_REDIRECT_URI"),
              postLogoutRedirectUri: str(env, "VITE_OIDC_POST_LOGOUT_REDIRECT_URI"),
            },
          }
        : {}),
    },
    apiBaseUrl: str(env, "VITE_API_BASE_URL") ?? "",
    appName: str(env, "VITE_APP_NAME") ?? defaults.appName,
  };
}
