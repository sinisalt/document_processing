import { describe, expect, it } from "vitest";
import { hasPolicy, hasRole, isPlatformRole, policiesOf, rolesByPolicy, Roles } from "./policies";

describe("hasPolicy", () => {
  it("mirrors the API's role table (README 'Authentication')", () => {
    expect(policiesOf([Roles.SuperAdmin])).toEqual(["PlatformAdmin", "Platform", "TenantAdmin", "Review", "Ingest", "Read"]);
    expect(policiesOf([Roles.Operator])).toEqual(["Platform", "Review", "Read"]);
    expect(policiesOf([Roles.TenantAdmin])).toEqual(["TenantAdmin", "Review", "Ingest", "Read"]);
    expect(policiesOf([Roles.Reviewer])).toEqual(["Review", "Ingest", "Read"]);
    expect(policiesOf([Roles.Viewer])).toEqual(["Read"]);
  });

  it("is case-sensitive like the API", () => {
    expect(hasPolicy(["superadmin"], "PlatformAdmin")).toBe(false);
    expect(hasPolicy(["SuperAdmin"], "PlatformAdmin")).toBe(true);
  });

  it("treats missing or empty roles as nothing", () => {
    expect(hasPolicy(undefined, "Read")).toBe(false);
    expect(hasPolicy(null, "Read")).toBe(false);
    expect(hasPolicy([], "Read")).toBe(false);
    expect(policiesOf([])).toEqual([]);
  });

  it("accepts any one of several roles", () => {
    expect(hasPolicy(["Viewer", "Reviewer"], "Review")).toBe(true);
    expect(hasPolicy(["Viewer", "Unknown"], "Review")).toBe(false);
  });

  it("lists every policy exactly once", () => {
    expect(Object.keys(rolesByPolicy)).toHaveLength(6);
  });
});

describe("hasRole and isPlatformRole", () => {
  it("matches any wanted role", () => {
    expect(hasRole(["Operator"], Roles.SuperAdmin, Roles.Operator)).toBe(true);
    expect(hasRole(["Viewer"], Roles.SuperAdmin, Roles.Operator)).toBe(false);
  });

  it("knows which roles have no tenant of their own", () => {
    expect(isPlatformRole("SuperAdmin")).toBe(true);
    expect(isPlatformRole("Operator")).toBe(true);
    expect(isPlatformRole("TenantAdmin")).toBe(false);
    expect(isPlatformRole("nope")).toBe(false);
  });
});
