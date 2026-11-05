import type { Destination, Transport } from "./api";

/** The auth variants of the http transport (docs/11 §13.3); mTLS comes later. */
export const authTypes = ["none", "apiKeyHeader", "basic", "bearer", "oauth2ClientCredentials", "hmacSha256"] as const;
export type AuthType = (typeof authTypes)[number];
export const httpMethods = ["POST", "PUT", "PATCH"] as const;

/**
 * The typed destination form (docs/11 §19 "a typed form per transport, write-only secret"): one flat set of fields,
 * of which a transport uses its own, turned into the `config` object the API checks (`DestinationConfig`).
 */
export interface DestinationFormValues {
  name: string;
  transport: Transport;
  method: (typeof httpMethods)[number];
  urlTemplate: string;
  timeoutSeconds: string;
  successCodes: string;
  authType: AuthType;
  headerName: string;
  username: string;
  tokenUrl: string;
  clientId: string;
  scope: string;
  /** One `Name: value` per line. */
  headers: string;
  responseMapping: string;
  updateMethod: "" | (typeof httpMethods)[number];
  updateUrlTemplate: string;
  /** Multipart: the rendered payload as `data` and the original file as `file`. */
  sendOriginal: boolean;
  path: string;
  fileNameTemplate: string;
  retentionDays: string;
  /** Write-only: empty keeps the stored secret. */
  secret: string;
}

export const emptyDestinationForm: DestinationFormValues = {
  name: "",
  transport: "http",
  method: "POST",
  urlTemplate: "",
  timeoutSeconds: "",
  successCodes: "",
  authType: "none",
  headerName: "",
  username: "",
  tokenUrl: "",
  clientId: "",
  scope: "",
  headers: "",
  responseMapping: "",
  updateMethod: "",
  updateUrlTemplate: "",
  sendOriginal: false,
  path: "",
  fileNameTemplate: "",
  retentionDays: "",
  secret: "",
};

/** Which auth variants need the destination's secret (the API key, password, token, client secret or HMAC key). */
export const authNeedsSecret = (type: AuthType) => type !== "none";

export type DestinationFormProblem = "headers" | "number";

/** `Name: value` lines as an object, or null when a line is not one. */
export function parseHeaders(text: string): Record<string, string> | null {
  const headers: Record<string, string> = {};
  for (const line of text.split("\n").map((l) => l.trim()).filter(Boolean)) {
    const colon = line.indexOf(":");
    if (colon <= 0) return null;
    headers[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
  }
  return headers;
}

const text = (value: unknown) => (typeof value === "string" ? value : value === undefined || value === null ? "" : String(value));

/** A stored destination as the form shows it; the secret is never sent back (`hasSecret` says whether one is set). */
export function formOf(destination: Destination | null): DestinationFormValues {
  if (!destination) return emptyDestinationForm;
  const config = (destination.config ?? {}) as Record<string, unknown>;
  const auth = (config.auth ?? {}) as Record<string, unknown>;
  const headers = (config.headers ?? {}) as Record<string, unknown>;
  const multipart = config.multipart as { parts?: Array<Record<string, unknown>> } | undefined;
  return {
    ...emptyDestinationForm,
    name: destination.name,
    transport: (destination.transport as Transport) ?? "http",
    method: (text(config.method) || "POST") as DestinationFormValues["method"],
    urlTemplate: text(config.urlTemplate),
    timeoutSeconds: text(config.timeoutSeconds),
    successCodes: Array.isArray(config.successCodes) ? config.successCodes.join(", ") : "",
    authType: (text(auth.type) || "none") as AuthType,
    headerName: text(auth.headerName),
    username: text(auth.username),
    tokenUrl: text(auth.tokenUrl),
    clientId: text(auth.clientId),
    scope: text(auth.scope),
    headers: Object.entries(headers)
      .map(([name, value]) => `${name}: ${text(value)}`)
      .join("\n"),
    responseMapping: text(config.responseMapping),
    updateMethod: text(config.updateMethod) as DestinationFormValues["updateMethod"],
    updateUrlTemplate: text(config.updateUrlTemplate),
    sendOriginal: !!multipart?.parts?.some((part) => part.content === "original"),
    path: text(config.path),
    fileNameTemplate: text(config.fileNameTemplate),
    retentionDays: text(config.retentionDays),
  };
}

const integer = (value: string) => (/^\d+$/.test(value.trim()) ? Number(value.trim()) : Number.NaN);

/**
 * The `config` of the form's transport, with only the settings that are filled (the API takes defaults for the rest),
 * or the problem that keeps it from being one.
 */
export function configOf(values: DestinationFormValues): { config: Record<string, unknown> } | { problem: DestinationFormProblem } {
  switch (values.transport) {
    case "http": {
      const config: Record<string, unknown> = { method: values.method, urlTemplate: values.urlTemplate.trim() };
      if (values.timeoutSeconds.trim()) {
        const seconds = integer(values.timeoutSeconds);
        if (Number.isNaN(seconds)) return { problem: "number" };
        config.timeoutSeconds = seconds;
      }
      if (values.successCodes.trim()) {
        const codes = values.successCodes.split(/[\s,]+/).filter(Boolean).map(integer);
        if (codes.some(Number.isNaN)) return { problem: "number" };
        config.successCodes = codes;
      }
      if (values.authType !== "none") {
        const auth: Record<string, unknown> = { type: values.authType };
        if (values.authType === "apiKeyHeader") auth.headerName = values.headerName.trim();
        if (values.authType === "basic") auth.username = values.username.trim();
        if (values.authType === "oauth2ClientCredentials") {
          auth.tokenUrl = values.tokenUrl.trim();
          auth.clientId = values.clientId.trim();
          if (values.scope.trim()) auth.scope = values.scope.trim();
        }
        config.auth = auth;
      }
      if (values.headers.trim()) {
        const headers = parseHeaders(values.headers);
        if (!headers) return { problem: "headers" };
        config.headers = headers;
      }
      if (values.responseMapping.trim()) config.responseMapping = values.responseMapping.trim();
      if (values.updateMethod) config.updateMethod = values.updateMethod;
      if (values.updateUrlTemplate.trim()) config.updateUrlTemplate = values.updateUrlTemplate.trim();
      if (values.sendOriginal) config.multipart = { parts: [{ name: "data", mapping: "self" }, { name: "file", content: "original" }] };
      return { config };
    }
    case "blob": {
      const config: Record<string, unknown> = { path: values.path.trim(), fileNameTemplate: values.fileNameTemplate.trim() };
      return { config };
    }
    case "pull": {
      const config: Record<string, unknown> = {};
      if (values.retentionDays.trim()) {
        const days = integer(values.retentionDays);
        if (Number.isNaN(days)) return { problem: "number" };
        config.retentionDays = days;
      }
      return { config };
    }
  }
}

/** Where the API names a config problem (`config.auth.headerName`), the form field that holds it. */
export function formFieldOfConfigPath(path: string): keyof DestinationFormValues | null {
  const normalized = path.replace(/^config\./i, "");
  const map: Record<string, keyof DestinationFormValues> = {
    method: "method",
    urlTemplate: "urlTemplate",
    updateMethod: "updateMethod",
    updateUrlTemplate: "updateUrlTemplate",
    timeoutSeconds: "timeoutSeconds",
    successCodes: "successCodes",
    "auth.headerName": "headerName",
    "auth.username": "username",
    "auth.tokenUrl": "tokenUrl",
    "auth.clientId": "clientId",
    "auth.type": "authType",
    auth: "authType",
    responseMapping: "responseMapping",
    multipart: "sendOriginal",
    path: "path",
    fileNameTemplate: "fileNameTemplate",
    retentionDays: "retentionDays",
    name: "name",
    transport: "transport",
    secret: "secret",
  };
  if (normalized.startsWith("headers")) return "headers";
  return map[normalized] ?? null;
}
