import createClient, { type Middleware } from "openapi-fetch";
import type { paths } from "./schema";

export type { paths, components, operations } from "./schema";

/** RFC 9457 problem details as the API returns them for every error. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
}

export interface ApiClientOptions {
  /** API origin, e.g. http://localhost:5100. Defaults to same origin. */
  baseUrl?: string;
  /** Returns the current OIDC access token, or null when signed out. */
  getAccessToken?: () => string | null | Promise<string | null>;
  /** Machine-client alternative to a token (X-Api-Key). */
  apiKey?: string;
  /** SuperAdmin only: act inside one tenant. */
  tenantId?: string | null;
  /** Called on 401 so the app can redirect to login. */
  onUnauthorized?: () => void;
}

/**
 * Typed client over the generated OpenAPI schema (openapi-fetch). No hand-written fetch code anywhere else
 * (docs/05 "API first"): `client.GET("/api/v1/me")` is fully typed from `src/schema.d.ts`.
 */
export function createApiClient(options: ApiClientOptions = {}) {
  const client = createClient<paths>({ baseUrl: options.baseUrl ?? "" });

  const auth: Middleware = {
    async onRequest({ request }) {
      if (options.apiKey) {
        request.headers.set("X-Api-Key", options.apiKey);
      } else if (options.getAccessToken) {
        const token = await options.getAccessToken();
        if (token) request.headers.set("Authorization", `Bearer ${token}`);
      }
      if (options.tenantId) request.headers.set("X-Tenant-Id", options.tenantId);
      return request;
    },
    async onResponse({ response }) {
      if (response.status === 401) options.onUnauthorized?.();
      return response;
    },
  };

  client.use(auth);
  return client;
}

export type ApiClient = ReturnType<typeof createApiClient>;

/** Extracts a human-readable message from a problem-details error body. */
export function describeProblem(problem: ProblemDetails | undefined, fallback = "Request failed"): string {
  if (!problem) return fallback;
  if (problem.errors) {
    const first = Object.entries(problem.errors)[0];
    if (first) return `${first[0]}: ${first[1].join(" ")}`;
  }
  return problem.detail ?? problem.title ?? fallback;
}
