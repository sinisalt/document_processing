import { describeProblem, type ProblemDetails } from "@docproc/api-client";

/**
 * The development identity endpoints (`src/DocProc.Api/Dev/DevEndpoints.cs`, docs/09 "Running without Docker").
 * They are mapped only while `Authentication:Dev:Enabled` is on and are not part of the exported `/api/v1` contract
 * (the OpenAPI document is exported with dev auth off), which is why this file is the one place with hand-written
 * fetch calls. Shapes follow the C# records: `DevUser` and the OAuth-style `DevTokenResponse`.
 */
export interface DevUser {
  username: string;
  email: string;
  firstName: string;
  lastName: string;
  displayName: string;
  subject: string;
  roles: string[];
  tenantId: string | null;
  /** The seeded name of the user's tenant (sign-in cards show it); null in platform scope. */
  tenantName?: string | null;
}

export interface DevTokenResponse {
  access_token: string;
  token_type: string;
  expires_in: number;
  user: DevUser;
}

export class DevAuthError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly problem?: ProblemDetails,
  ) {
    super(message);
    this.name = "DevAuthError";
  }
}

async function problemOf(response: Response): Promise<ProblemDetails | undefined> {
  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return undefined;
  }
}

export async function listDevUsers(baseUrl = "", fetchImpl: typeof fetch = fetch): Promise<DevUser[]> {
  const response = await fetchImpl(`${baseUrl}/dev/users`, { headers: { accept: "application/json" } });
  if (!response.ok) {
    throw new DevAuthError(
      response.status === 404 ? "Development sign-in is not enabled on this API." : describeProblem(await problemOf(response), `Could not load users (${response.status})`),
      response.status,
    );
  }
  return (await response.json()) as DevUser[];
}

export async function issueDevToken(username: string, baseUrl = "", fetchImpl: typeof fetch = fetch): Promise<DevTokenResponse> {
  const response = await fetchImpl(`${baseUrl}/dev/token`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ username }),
  });
  if (!response.ok) {
    const problem = await problemOf(response);
    throw new DevAuthError(describeProblem(problem, `Sign-in failed (${response.status})`), response.status, problem);
  }
  return (await response.json()) as DevTokenResponse;
}
