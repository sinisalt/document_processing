import { readShellConfig } from "@docproc/app-shell";

/** VITE_AUTH_MODE, VITE_OIDC_*, VITE_API_BASE_URL, VITE_APP_NAME from the .env files of this app. */
export const shellConfig = readShellConfig(import.meta.env, { appName: "DocProc Admin" });
