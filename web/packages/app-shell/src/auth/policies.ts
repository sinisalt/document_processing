/**
 * Roles and policies exactly as `src/DocProc.Api/Auth/Roles.cs` defines them (docs/05 "Roles"). The server is the
 * authority (`GET /api/v1/me` returns the satisfied `permissions`); this mirror lets the UI hide what a role can never do
 * without a round trip, and `useAuth().hasPolicy` prefers the server's list when it is available.
 */
export const Roles = {
  SuperAdmin: "SuperAdmin",
  Operator: "Operator",
  TenantAdmin: "TenantAdmin",
  Reviewer: "Reviewer",
  Viewer: "Viewer",
} as const;

export type Role = (typeof Roles)[keyof typeof Roles];

export const Policies = {
  PlatformAdmin: "PlatformAdmin",
  Platform: "Platform",
  TenantAdmin: "TenantAdmin",
  Review: "Review",
  Ingest: "Ingest",
  Read: "Read",
} as const;

export type Policy = (typeof Policies)[keyof typeof Policies];

export const allRoles: readonly Role[] = [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer, Roles.Viewer];

/** Roles that work in platform scope (no tenant of their own). */
export const platformRoles: readonly Role[] = [Roles.SuperAdmin, Roles.Operator];

export const rolesByPolicy: Readonly<Record<Policy, readonly Role[]>> = {
  PlatformAdmin: [Roles.SuperAdmin],
  Platform: [Roles.SuperAdmin, Roles.Operator],
  TenantAdmin: [Roles.SuperAdmin, Roles.TenantAdmin],
  Review: [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer],
  Ingest: [Roles.SuperAdmin, Roles.TenantAdmin, Roles.Reviewer],
  Read: [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer, Roles.Viewer],
};

export function isRole(value: string): value is Role {
  return (allRoles as readonly string[]).includes(value);
}

export function isPlatformRole(role: string): boolean {
  return (platformRoles as readonly string[]).includes(role);
}

/** True when the principal holds at least one of the wanted roles (case-sensitive, like the API). */
export function hasRole(roles: readonly string[] | null | undefined, ...wanted: readonly Role[]): boolean {
  if (!roles || roles.length === 0) return false;
  return wanted.some((role) => roles.includes(role));
}

/**
 * Whether a set of roles satisfies a policy by the API's rule (`Policies.Satisfies`): any listed role. API-key scopes
 * never reach the browser, so they are not modelled here.
 */
export function hasPolicy(roles: readonly string[] | null | undefined, policy: Policy): boolean {
  return hasRole(roles, ...rolesByPolicy[policy]);
}

/** Policies a set of roles satisfies, in the API's order. */
export function policiesOf(roles: readonly string[] | null | undefined): Policy[] {
  return (Object.keys(rolesByPolicy) as Policy[]).filter((policy) => hasPolicy(roles, policy));
}
