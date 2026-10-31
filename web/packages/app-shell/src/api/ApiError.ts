import i18n from "i18next";
import { describeProblem, type ProblemDetails, type ValidationErrorCode } from "@docproc/api-client";
import { formatNumber } from "../lib/format";

/**
 * A field error's code in the UI language (`common:apiErrors.<code>` with its parameters), or null when this build
 * has no text for the code or the text needs a parameter the API did not send: the caller shows the English message.
 */
export function validationCodeText(error: ValidationErrorCode, language?: string): string | null {
  if (!i18n.isInitialized) return null;
  const key = `apiErrors.${error.code}`;
  if (!i18n.exists(key, { ns: "common", lng: language })) return null;
  const values: Record<string, string> = {};
  for (const [name, raw] of Object.entries(error.params ?? {})) values[name] = raw !== "" && Number.isFinite(Number(raw)) ? formatNumber(raw, language) : raw;
  let missing = false;
  const text = i18n.t(key, {
    ns: "common",
    lng: language,
    ...values,
    missingInterpolationHandler: () => {
      missing = true;
      return "";
    },
  } as Record<string, unknown>);
  return missing || typeof text !== "string" || text.length === 0 ? null : text;
}

/** The problem's field errors with every coded one in the UI language; the rest (and unknown codes) stay English. */
export function localizedFieldErrors(problem: ProblemDetails | undefined, language?: string): Record<string, string[]> {
  const errors = problem?.errors ?? {};
  const codes = problem?.errorCodes ?? {};
  const result: Record<string, string[]> = {};
  for (const [field, messages] of Object.entries(errors)) {
    const coded = codes[field];
    const text = coded ? validationCodeText(coded, language) : null;
    result[field] = text ? [text] : messages;
  }
  return result;
}

/**
 * openapi-fetch never throws on an HTTP error; it returns `{ error, response }`. TanStack Query needs a thrown error to
 * reach `isError`, retries and the global `onError`, so every query/mutation function goes through `unwrap`.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | undefined;
  readonly url: string;

  constructor(status: number, problem: ProblemDetails | undefined, url: string) {
    super(describeProblem(problem?.errors ? { ...problem, errors: localizedFieldErrors(problem) } : problem, statusFallback(status)));
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
    this.url = url;
  }

  /** The problem's `title` (e.g. "Document changed", "Task already claimed"), falling back to the message. */
  get title(): string {
    return this.problem?.title ?? this.message;
  }

  /**
   * Field errors of a 400 validation problem, keyed by the API's field or path name: in the UI language where the
   * API sent a code for the field (`errorCodes`), its English text otherwise.
   */
  get fieldErrors(): Record<string, string[]> {
    return localizedFieldErrors(this.problem);
  }

  /**
   * What the global toast shows for a 400 validation problem: a translated title (ASP.NET's title is always the English
   * "One or more validation errors occurred.") and the first field error in the UI language, without the API's field key.
   */
  get validationSummary(): { title: string; description: string | undefined } {
    const first = Object.values(this.fieldErrors)[0];
    return {
      title: i18n.t("errors.validation", { ns: "common", defaultValue: "Please check your input." }),
      description: first && first.length > 0 ? first.join(" ") : (this.problem?.detail ?? undefined),
    };
  }

  get isValidation(): boolean {
    return this.status === 400 && !!this.problem?.errors;
  }

  get isConflict(): boolean {
    return this.status === 409;
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError;
}

function statusFallback(status: number): string {
  switch (status) {
    case 401:
      return "Not signed in";
    case 403:
      return "Not allowed";
    case 404:
      return "Not found";
    case 409:
      return "Conflict";
    default:
      return `Request failed (${status})`;
  }
}

export interface FetchResult<T> {
  data?: T;
  error?: unknown;
  response: Response;
}

/** Returns the data of a successful openapi-fetch call or throws an `ApiError` built from the problem body. */
export function unwrap<T>(result: FetchResult<T>): T {
  if (result.error !== undefined || !result.response.ok) {
    const problem = typeof result.error === "object" && result.error !== null ? (result.error as ProblemDetails) : undefined;
    throw new ApiError(result.response.status, problem, result.response.url);
  }
  return result.data as T;
}

/** A message for any error a query can produce: problem details, network failure, or something unexpected. */
export function errorMessage(error: unknown, fallback = "Something went wrong"): string {
  if (error instanceof ApiError) return error.message;
  if (error instanceof TypeError) return "The server could not be reached.";
  if (error instanceof Error) return error.message || fallback;
  return fallback;
}
