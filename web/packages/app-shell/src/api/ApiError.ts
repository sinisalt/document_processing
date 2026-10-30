import { describeProblem, type ProblemDetails } from "@docproc/api-client";

/**
 * openapi-fetch never throws on an HTTP error; it returns `{ error, response }`. TanStack Query needs a thrown error to
 * reach `isError`, retries and the global `onError`, so every query/mutation function goes through `unwrap`.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | undefined;
  readonly url: string;

  constructor(status: number, problem: ProblemDetails | undefined, url: string) {
    super(describeProblem(problem, statusFallback(status)));
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
    this.url = url;
  }

  /** The problem's `title` (e.g. "Document changed", "Task already claimed"), falling back to the message. */
  get title(): string {
    return this.problem?.title ?? this.message;
  }

  /** Field errors of a 400 validation problem, keyed by the API's field or path name. */
  get fieldErrors(): Record<string, string[]> {
    return this.problem?.errors ?? {};
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
